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
        StringAssert.Contains(_t.Outbox.Enqueued[^1].Message.Message.Markdown, "💬 **1 new review comment(s)**: fix round 1");
        StringAssert.Contains(JobEventMessages.For(_t.Jobs.SavedEvents.OfType<FixRoundStarted>().Single())!.Message.Markdown, "**Fix round 1:** 1 thing(s) to fix");

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
    public async Task A_chat_message_during_review_starts_a_fix_round_like_a_comment()
    {
        var job = await InReviewAsync();

        var outcome = await new SubmitDeveloperMessageHandler(_t.Jobs).Handle(new SubmitDeveloperMessage(job, "also update the README", "tngo"), default);

        Assert.AreEqual(DeveloperMessageOutcome.FixRound, outcome.Value);
        var j = _t.Jobs.Get(job);
        Assert.AreEqual((JobState.Running, 1), (j.State, j.FixRounds));
        CollectionAssert.AreEqual(new[] { "tngo (in chat): also update the README" }, j.PendingMessages.ToArray(), "the agent resumes with it");
    }

    [TestMethod]
    [DataRow("https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/3935", 3935)]
    [DataRow("https://dev.azure.com/o/p/_git/r/pullrequest/7/", 7)]
    public void The_pr_id_comes_from_its_url(string url, int id) =>
        Assert.AreEqual(id, ReviewPullRequestsHandler.PullRequestId(new Uri(url)));

    [TestMethod]
    public async Task A_pr_merged_during_a_fix_round_ends_the_job_without_opening_a_second_pr()
    {
        var job = await InReviewAsync();
        _t.PullRequests.Comments.Add(Comment(thread: 10, id: 1, "One more thing"));
        await Review();
        await new ResumeJobTurnHandler(_t.Jobs, _t.Outbox).Handle(new ResumeJobTurn([]), default);   // the agent gets the comment
        _t.PullRequests.Status = PullRequestStatus.Completed;   // merged while the agent works on the round

        var finished = await _t.Finish().Handle(new FinishWork(job, "T", "D", "S"), default);

        Assert.AreEqual("not_published", finished.Error!.Code);
        Assert.HasCount(1, _t.PullRequests.Created, "only the first PR, never a second one for the same work item");
        Assert.AreEqual(JobState.Done, _t.Jobs.Get(job).State);
        StringAssert.Contains(_t.Outbox.Enqueued[^1].Message.Message.Markdown, "The PR was already merged");
    }

    [TestMethod]
    public async Task A_pr_merged_during_a_fix_round_still_gets_its_hand_off()
    {
        _t.Options.Value.Handoff = true;
        var job = await InReviewAsync();
        _t.PullRequests.Comments.Add(Comment(thread: 10, id: 1, "One more thing"));
        await Review();
        await new ResumeJobTurnHandler(_t.Jobs, _t.Outbox).Handle(new ResumeJobTurn([]), default);
        _t.PullRequests.Status = PullRequestStatus.Completed;   // merged while the agent works on the round

        var finished = await _t.Finish().Handle(new FinishWork(job, "T", "D", "S"), default);

        Assert.AreEqual(PublishPullRequestHandler.NotPublished, finished.Error!.Code);
        Assert.HasCount(1, _t.PullRequests.Created, "still no second PR");
        Assert.AreEqual(HandoffStatus.Requested, _t.Jobs.Get(job).Handoff, "the hand-off follows, as after a merge seen in review");
        StringAssert.Contains(_t.Outbox.Enqueued.Select(e => e.Message.Message.Markdown).Last(m => m.Contains("already merged", StringComparison.Ordinal)), "Next: the knowledge hand-off");
    }

    [TestMethod]
    public async Task A_pr_abandoned_during_a_fix_round_cancels_the_job_and_says_so()
    {
        var job = await InReviewAsync();
        _t.PullRequests.Comments.Add(Comment(thread: 10, id: 1, "One more thing"));
        await Review();
        await new ResumeJobTurnHandler(_t.Jobs, _t.Outbox).Handle(new ResumeJobTurn([]), default);
        _t.PullRequests.Status = PullRequestStatus.Abandoned;

        await _t.Finish().Handle(new FinishWork(job, "T", "D", "S"), default);

        Assert.HasCount(1, _t.PullRequests.Created);
        Assert.AreEqual(JobState.Cancelled, _t.Jobs.Get(job).State);
        StringAssert.Contains(_t.Outbox.Enqueued[^1].Message.Message.Markdown, "The PR was abandoned");
    }

    [TestMethod]
    public async Task A_chat_message_after_the_merge_starts_no_fix_round()
    {
        var job = await InReviewAsync();
        _t.PullRequests.Status = PullRequestStatus.Completed;   // merged between two review polls
        var submit = new SubmitDeveloperMessageHandler(_t.Jobs, null, _t.PullRequests, _t.Registry);

        Assert.AreEqual(DeveloperMessageOutcome.Merged, (await submit.Handle(new SubmitDeveloperMessage(job, "merged", "tngo"), default)).Value);
        Assert.AreEqual((JobState.InReview, 0), (_t.Jobs.Get(job).State, _t.Jobs.Get(job).FixRounds), "the review loop ends it; nothing runs");

        _t.PullRequests.Status = PullRequestStatus.Active;
        Assert.AreEqual(DeveloperMessageOutcome.FixRound, (await submit.Handle(new SubmitDeveloperMessage(job, "rename it", "tngo"), default)).Value, "an open PR still takes feedback");
    }

    [TestMethod]
    public async Task A_failed_pr_build_starts_one_fix_round_with_its_errors_and_a_new_failed_run_another()
    {
        var search = new Builds();
        _t.Search = search;
        var job = await InReviewAsync();
        search.Latest = Build(901, "failed");

        Assert.AreEqual(1, (await Review()).Value);

        var running = _t.Jobs.Get(job);
        Assert.AreEqual((JobState.Running, 1), (running.State, running.FixRounds));
        var feedback = running.PendingMessages.Single();
        StringAssert.StartsWith(feedback, "The PR build failed (sysmin-ci, run 901).");
        StringAssert.Contains(feedback, "- dotnet test: exit code 1\n```\nFailed X\n```");
        StringAssert.Contains(_t.Outbox.Enqueued[^1].Message.Message.Markdown, "💬 **The PR build failed**: fix round 1");
        await new ResumeJobTurnHandler(_t.Jobs, _t.Outbox).Handle(new ResumeJobTurn([]), default);
        Assert.IsTrue((await _t.Finish().Handle(new FinishWork(job, "T", "D", "S"), default)).IsSuccess);

        await Review();
        Assert.AreEqual(1, _t.Jobs.Get(job).FixRounds, "the same run isn't handled twice");
        search.Latest = Build(902, "succeeded");
        await Review();
        Assert.AreEqual(1, _t.Jobs.Get(job).FixRounds, "a passing run starts nothing");
        search.Latest = Build(903, "failed");
        await Review();
        Assert.AreEqual(2, _t.Jobs.Get(job).FixRounds, "the next failed run is a new round");
    }

    [TestMethod]
    public async Task Merge_conflicts_start_one_round_per_pr_head_telling_the_agent_to_merge_not_rebase()
    {
        var job = await InReviewAsync();
        _t.PullRequests.Details[77] = new PullRequestDetails(77, "T", null, "agentd", "ai/5617-x", "develop", "h1", PullRequestStatus.Active, false, new Uri("https://x/pr/77"), MergeStatus: "conflicts");
        _t.PullRequests.Comments.Add(Comment(thread: 10, id: 1, "Rename it"));

        await Review();

        var running = _t.Jobs.Get(job);
        Assert.AreEqual(2, running.PendingMessages.Count, "the comment and the conflicts, one round");
        StringAssert.Contains(running.PendingMessages[1], "`git merge origin/develop`");
        StringAssert.Contains(running.PendingMessages[1], "Never rebase");
        StringAssert.Contains(_t.Outbox.Enqueued[^1].Message.Message.Markdown, "💬 **1 new review comment(s), merge conflicts**: fix round 1");
        await new ResumeJobTurnHandler(_t.Jobs, _t.Outbox).Handle(new ResumeJobTurn([]), default);
        Assert.IsTrue((await _t.Finish().Handle(new FinishWork(job, "T", "D", "S"), default)).IsSuccess);

        await Review();
        Assert.AreEqual(1, _t.Jobs.Get(job).FixRounds, "the same head's conflicts aren't handled twice");
        _t.PullRequests.Details[77] = _t.PullRequests.Details[77] with { SourceCommit = "h2" };
        await Review();
        Assert.AreEqual(2, _t.Jobs.Get(job).FixRounds, "still conflicting after a push: another round");
    }

    [TestMethod]
    public async Task Past_the_round_limit_a_failed_build_asks_once_to_take_over()
    {
        _t.Options.Value.MaxFixRounds = 0;
        var search = new Builds { Latest = Build(901, "failed") };
        _t.Search = search;
        var job = await InReviewAsync();

        await Review();
        await Review();

        Assert.AreEqual(JobState.InReview, _t.Jobs.Get(job).State);
        Assert.AreEqual(1, _t.Outbox.Enqueued.Count(e => e.Message.Message.Markdown.Contains("Please take over", StringComparison.Ordinal)));
        StringAssert.Contains(_t.Outbox.Enqueued[^1].Message.Message.Markdown, "⚠️ **The PR build failed**, but the job already ran 0 fix rounds");
    }

    private static BuildHit Build(int id, string result) => new(id, "sysmin-ci", "1", "completed", result, "refs/pull/77/merge", null, "pullRequest", "h1", null, null);

    private sealed class Builds : IAzureDevOpsSearch
    {
        public BuildHit? Latest { get; set; }

        public Task<IReadOnlyList<BuildHit>> ListBuildsAsync(string? pipeline, string? branch, string? result, int top, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BuildHit>>(Latest is null || branch != "refs/pull/77/merge" ? [] : [Latest]);

        public Task<BuildDetail?> GetBuildAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult<BuildDetail?>(new BuildDetail(Latest!, [new BuildFailure("dotnet test", "Task", "failed", ["exit code 1"], "Failed X")]));

        public Task<IReadOnlyList<WorkItemHit>> SearchWorkItemsAsync(WorkItemQuery query, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PullRequestHit>> ListPullRequestsAsync(string? repository, string status, int top, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<PipelineHit>> ListPipelinesAsync(string? name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<WikiHit>> SearchWikiAsync(string text, int top, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<WikiPage?> GetWikiPageAsync(string? wiki, string? path, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private async Task<Domain.Jobs.ValueObjects.JobId> InReviewAsync()
    {
        var request = await _t.RunningJobAsync();
        Assert.IsTrue((await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default)).IsSuccess);
        return request.JobId;
    }

    private Task<Domain.Common.Result<int>> Review() =>
        _t.Review().Handle(new ReviewPullRequests(), default);

    private static PullRequestComment Comment(int thread, int id, string text, string? file = null, int? line = null, string status = "active") =>
        new(thread, id, "Reviewer", text, file, line, status, DateTimeOffset.UnixEpoch.AddMinutes(id));
}
