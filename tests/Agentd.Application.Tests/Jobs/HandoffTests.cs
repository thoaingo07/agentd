using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.Events;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class HandoffTests
{
    private readonly TestContext _t = new();

    public HandoffTests()
    {
        _t.Options.Value.ReviewLoop = true;
        _t.Options.Value.Handoff = true;
    }

    [TestMethod]
    public async Task A_merged_pr_starts_the_handoff_in_the_same_worktree_on_a_knowledge_branch()
    {
        var job = await MergedAsync();

        var running = _t.Jobs.Get(job);
        Assert.AreEqual((JobState.Running, HandoffStatus.Requested), (running.State, running.Handoff));
        Assert.AreEqual("ai/1234-knowledge", running.Branch?.Value);
        CollectionAssert.AreEqual(new[] { "ai/1234-knowledge" }, _t.Worktrees.Recreated);
        Assert.AreEqual("/home/agentd/.agentd/worktrees/sysmin/wi-1234", running.Worktree?.Value, "same path, so the Claude session resumes");
        Assert.IsTrue(_t.Outbox.Enqueued.Any(e => e.Message.Message.Markdown.StartsWith("🎉 **PR merged:**", StringComparison.Ordinal)));
        Assert.IsTrue(_t.Jobs.SavedEvents.OfType<HandoffStarted>().Any());

        var turn = (await Resume()).Value!;
        Assert.IsTrue(turn.Resume);
        Assert.IsTrue(turn.ReadOnly, "read-only until the developer agrees");
        StringAssert.Contains(turn.Prompt, "was merged");
        StringAssert.Contains(turn.Prompt, "propose_knowledge");
        Assert.AreEqual(HandoffStatus.Proposing, _t.Jobs.Get(job).Handoff);
    }

    [TestMethod]
    public async Task Proposals_are_revised_until_the_developer_agrees_then_the_sync_pr_is_reviewed_and_merged()
    {
        var job = await MergedAsync();
        await Resume();
        Assert.AreEqual("handoff_not_agreed", (await _t.Finish().Handle(new FinishWork(job, "T", "D", "S"), default)).Error?.Code);

        await Propose(job, "AGENTS.md › DataSync: add the 02:00 CronJob note.");
        var question = (DeveloperQuestionAsked)_t.Jobs.SavedEvents.Last(e => e is DeveloperQuestionAsked);
        CollectionAssert.AreEqual(new[] { ProposeKnowledgeHandler.SyncLabel, ProposeKnowledgeHandler.ChangeLabel, ProposeKnowledgeHandler.SkipLabel }, question.Options.ToArray());

        await Reply(job, "also note the Key Vault login");
        var revise = (await Resume()).Value!;
        Assert.IsTrue(revise.ReadOnly);
        StringAssert.Contains(revise.Prompt, "Revise your knowledge proposal");

        await Propose(job, "AGENTS.md › DataSync: CronJob note + Key Vault login.");
        await Reply(job, ProposeKnowledgeHandler.SyncLabel);
        var write = (await Resume()).Value!;
        Assert.IsFalse(write.ReadOnly);
        StringAssert.Contains(write.Prompt, "make exactly the proposed changes");

        Assert.IsTrue((await _t.Finish().Handle(new FinishWork(job, "Sync knowledge from WI-1234", "D", "S"), default)).IsSuccess);
        Assert.AreEqual(JobState.InReview, _t.Jobs.Get(job).State, "the sync PR goes through review too");
        _t.PullRequests.Status = PullRequestStatus.Completed;
        await Review();

        Assert.IsTrue(_t.Outbox.Enqueued.Any(e => e.Message.Message.Markdown.StartsWith("🎓 **Knowledge synced.**", StringComparison.Ordinal)));
        AssertClosingOut(job);
    }

    [TestMethod]
    [DataRow("1", true)]
    [DataRow("🗑 Delete thread", true)]
    [DataRow("keep", false)]
    public async Task The_close_out_deletes_or_keeps_the_thread_on_the_developers_word(string answer, bool deleted)
    {
        var chat = new Fakes.FakeChat("discord");
        _t.Chats.Add(chat);
        _t.Messaging.Providers["discord"] = new Application.Messaging.MessagingProviderSettings { Enabled = true };
        var job = await MergedAsync();
        var thread = $"thread-{job}";
        await Resume();
        await Propose(job, "AGENTS.md: one note.");
        await Reply(job, ProposeKnowledgeHandler.SkipLabel);

        Assert.IsTrue((await _t.AnswerCloseOut().Handle(new AnswerCloseOut(job, "what?"), default)).Value, "handled: asked again");
        Assert.AreEqual(JobState.WaitingForHuman, _t.Jobs.Get(job).State);
        Assert.IsTrue((await _t.AnswerCloseOut().Handle(new AnswerCloseOut(job, answer), default)).Value);

        Assert.AreEqual(JobState.Done, _t.Jobs.Get(job).State);
        Assert.AreEqual(deleted, chat.DeletedThreads.Contains(thread));
        Assert.AreEqual(!deleted, chat.ArchivedThreads.Contains(thread));
        Assert.IsFalse(_t.Conversations.All.Single().IsOpen);
    }

    [TestMethod]
    public async Task Declining_finishes_the_job_without_a_sync()
    {
        var job = await MergedAsync();
        await Resume();
        await Propose(job, "AGENTS.md: one note.");

        var outcome = await Reply(job, ProposeKnowledgeHandler.SkipLabel);

        Assert.AreEqual(DeveloperMessageOutcome.HandoffDeclined, outcome);
        AssertClosingOut(job);
        Assert.IsNull((await Resume()).Value, "no further agent turn");
    }

    [TestMethod]
    public async Task A_job_finished_before_the_review_loop_can_hand_off_on_request()
    {
        _t.Options.Value.ReviewLoop = false;
        var request = await _t.RunningJobAsync();
        await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default);
        Assert.AreEqual(JobState.Done, _t.Jobs.Get(request.JobId).State);

        Assert.IsTrue((await _t.StartHandoff().Handle(new StartHandoff(request.JobId), default)).IsSuccess);
        Assert.AreEqual("validation", (await _t.StartHandoff().Handle(new StartHandoff(request.JobId), default)).Error?.Code, "once per job");
        Assert.AreEqual(HandoffStatus.Requested, _t.Jobs.Get(request.JobId).Handoff);
    }

    [TestMethod]
    [DataRow("3", false, true)]
    [DataRow("🚫 Don't sync", false, true)]
    [DataRow("don't sync, thanks", false, true)]
    [DataRow("skip", false, true)]
    [DataRow("1", true, false)]
    [DataRow("✅ Sync these changes", true, false)]
    [DataRow("sync it", true, false)]
    [DataRow("lgtm", true, false)]
    [DataRow("also mention the port", false, false)]
    public void Replies_to_the_proposal_are_recognized(string reply, bool agrees, bool declines)
    {
        Assert.AreEqual(agrees, ProposeKnowledgeHandler.IsAgreement(reply));
        Assert.AreEqual(declines, ProposeKnowledgeHandler.IsDecline(reply));
    }

    private void AssertClosingOut(JobId job)
    {
        var closing = _t.Jobs.Get(job);
        Assert.AreEqual((JobState.WaitingForHuman, HandoffStatus.Closing), (closing.State, closing.Handoff));
        var question = (DeveloperQuestionAsked)_t.Jobs.SavedEvents.Last(e => e is DeveloperQuestionAsked);
        StringAssert.StartsWith(question.Question, "🧹 **All done.** Delete this thread?");
    }

    private async Task<JobId> MergedAsync()
    {
        var request = await _t.RunningJobAsync();
        await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default);
        _t.PullRequests.Status = PullRequestStatus.Completed;
        await Review();
        _t.PullRequests.Status = PullRequestStatus.Active;
        return request.JobId;
    }

    private Task<Domain.Common.Result<AgentRunRequest?>> Resume() =>
        new ResumeJobTurnHandler(_t.Jobs, _t.Outbox).Handle(new ResumeJobTurn([]), default);

    private async Task Propose(JobId job, string proposal) =>
        Assert.IsTrue((await new ProposeKnowledgeHandler(_t.Jobs).Handle(new ProposeKnowledge(job, proposal), default)).IsSuccess);

    private async Task<DeveloperMessageOutcome> Reply(JobId job, string text) =>
        (await new SubmitDeveloperMessageHandler(_t.Jobs, _t.RequestCloseOut()).Handle(new SubmitDeveloperMessage(job, text, "tngo"), default)).Value;

    private Task<Domain.Common.Result<int>> Review() =>
        _t.Review()
            .Handle(new ReviewPullRequests(), default);
}
