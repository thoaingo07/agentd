using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.Events;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class ReviewLoopTests
{
    private readonly TestContext _t = new();

    public ReviewLoopTests() => _t.Options.Value.ReviewLoop = true;

    [TestMethod]
    public async Task Opening_the_pr_puts_the_job_in_review_and_keeps_the_worktree()
    {
        var job = await InReviewAsync();

        Assert.AreEqual(JobState.InReview, _t.Jobs.Get(job).State);
        Assert.IsEmpty(_t.Worktrees.Removed);
    }

    [TestMethod]
    public async Task A_new_review_comment_starts_a_fix_round_and_the_push_replies_on_its_thread()
    {
        var job = await InReviewAsync();
        _t.PullRequests.Comments.Add(Comment(thread: 10, id: 1, "Please mention the 02:00 CronJob.", file: "/AGENTS.md", line: 42));

        Assert.AreEqual(1, (await Review()).Value);

        var running = _t.Jobs.Get(job);
        Assert.AreEqual((JobState.Running, 1), (running.State, running.FixRounds));
        CollectionAssert.AreEqual(new[] { "Reviewer on AGENTS.md:42 [PR thread 10]: Please mention the 02:00 CronJob." }, running.PendingMessages.ToArray());
        StringAssert.Contains(_t.Outbox.Enqueued[^1].Message.Message.Markdown, "💬 **Review comments**");
        StringAssert.Contains(JobEventMessages.For(_t.Jobs.SavedEvents.OfType<FixRoundStarted>().Single())!.Message.Markdown, "**Fix round 1:** 1 review comment(s)");

        var turn = (await new ResumeJobTurnHandler(_t.Jobs, _t.Outbox).Handle(new ResumeJobTurn([]), default)).Value!;
        StringAssert.Contains(turn.Prompt, "If these are PR review comments");
        Assert.IsTrue((await _t.Finish().Handle(new FinishWork(job, "T", "D", "S"), default)).IsSuccess);

        Assert.AreEqual(JobState.InReview, _t.Jobs.Get(job).State);
        Assert.AreEqual((77, 10, "Addressed in the latest push (fix round 1). Please review."), _t.PullRequests.Replies.Single());
        await Review();
        Assert.AreEqual(1, _t.Jobs.Get(job).FixRounds, "the handled comment does not start another round");
    }

    [TestMethod]
    public async Task With_every_thread_resolved_ready_is_announced_once()
    {
        var job = await InReviewAsync();
        _t.PullRequests.Comments.Add(Comment(thread: 11, id: 3, "Typo", status: "fixed"));

        await Review();
        await Review();

        Assert.HasCount(1, _t.Jobs.SavedEvents.OfType<ReadyToComplete>());
        Assert.AreEqual(JobState.InReview, _t.Jobs.Get(job).State);
        CollectionAssert.Contains(_t.Jobs.Get(job).Review.SeenCommentIds.ToArray(), 3, "a resolved comment never starts a round");
    }

    [TestMethod]
    public async Task A_merged_pr_finishes_the_job_and_removes_the_worktree()
    {
        var job = await InReviewAsync();
        _t.PullRequests.Status = PullRequestStatus.Completed;

        await Review();

        Assert.AreEqual(JobState.Done, _t.Jobs.Get(job).State);
        Assert.HasCount(1, _t.Worktrees.Removed);
        StringAssert.Contains(JobEventMessages.For(_t.Jobs.SavedEvents.OfType<PullRequestMerged>().Single())!.Message.Markdown, "🎉 **PR merged:**");
    }

    [TestMethod]
    public async Task An_abandoned_pr_cancels_the_job()
    {
        var job = await InReviewAsync();
        _t.PullRequests.Status = PullRequestStatus.Abandoned;

        await Review();

        Assert.AreEqual(JobState.Cancelled, _t.Jobs.Get(job).State);
        Assert.AreEqual("PR abandoned", _t.Jobs.SavedEvents.OfType<JobCancelled>().Single().By);
    }

    [TestMethod]
    public async Task After_the_round_limit_agentd_asks_the_developer_to_take_over()
    {
        _t.Options.Value.MaxFixRounds = 0;
        var job = await InReviewAsync();
        _t.PullRequests.Comments.Add(Comment(thread: 10, id: 1, "One more thing"));

        await Review();

        Assert.AreEqual(JobState.InReview, _t.Jobs.Get(job).State);
        StringAssert.Contains(_t.Outbox.Enqueued[^1].Message.Message.Markdown, "Please take over");
    }

    [TestMethod]
    [DataRow("https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/3935", 3935)]
    [DataRow("https://dev.azure.com/o/p/_git/r/pullrequest/7/", 7)]
    public void The_pr_id_comes_from_its_url(string url, int id) =>
        Assert.AreEqual(id, ReviewPullRequestsHandler.PullRequestId(new Uri(url)));

    private async Task<Domain.Jobs.ValueObjects.JobId> InReviewAsync()
    {
        var request = await _t.RunningJobAsync();
        Assert.IsTrue((await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default)).IsSuccess);
        return request.JobId;
    }

    private Task<Domain.Common.Result<int>> Review() =>
        new ReviewPullRequestsHandler(_t.Jobs, _t.Registry, _t.PullRequests, _t.Worktrees, _t.Outbox, _t.Options).Handle(new ReviewPullRequests(), default);

    private static PullRequestComment Comment(int thread, int id, string text, string? file = null, int? line = null, string status = "active") =>
        new(thread, id, "Reviewer", text, file, line, status, DateTimeOffset.UnixEpoch.AddMinutes(id));
}
