using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class PublishTests
{
    [TestMethod]
    public async Task The_pr_title_gets_the_work_item_suffix_and_the_description_a_footer()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync(1234);

        await t.Finish().Handle(new FinishWork(request.JobId, "Fix login redirect", "Changed the redirect.", "Done"), CancellationToken.None);

        Assert.AreEqual("Fix login redirect (WI-1234)", t.PullRequests.Created.Single().Title);
    }

    [TestMethod]
    public void The_suffix_is_not_duplicated_and_the_footer_links_the_job()
    {
        var job = Job.Create(Domain.Jobs.ValueObjects.WorkItemId.From(7), Domain.Jobs.ValueObjects.RepositoryName.From("r"), "t", new Fakes.FakeClock());
        var draft = Domain.Jobs.ValueObjects.PullRequestDraft.Create("Add audit log (WI-7)", "Body", "Sum").Value!;

        Assert.AreEqual("Add audit log (WI-7)", PublishPullRequestHandler.Title(job, draft));
        StringAssert.Contains(PublishPullRequestHandler.Description(job, draft), "Body\n\n---\nWork item: #7");
        StringAssert.Contains(PublishPullRequestHandler.Description(job, draft), "Created by agentd");
    }

    [TestMethod]
    public async Task No_commits_fails_the_job_and_tells_the_work_item()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        t.Worktrees.HasCommits = false;

        await t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), CancellationToken.None);

        Assert.AreEqual(JobState.Failed, t.Jobs.Get(request.JobId).State);
        Assert.IsTrue(t.WorkItems.Comments.Any(c => c.Text.Contains("without committing", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task A_push_failure_keeps_the_job_publishing_and_a_retry_succeeds()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        t.Worktrees.FailPushes = 1;

        var first = await t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), CancellationToken.None);

        Assert.IsFalse(first.IsSuccess);
        var waiting = t.Jobs.Get(request.JobId);
        Assert.AreEqual(JobState.Publishing, waiting.State);
        Assert.AreEqual(1, waiting.PublishAttempts);
        Assert.AreEqual(t.Clock.UtcNow + t.Options.Value.PublishRetryDelay, waiting.NotBefore);
        StringAssert.Contains(waiting.LastError, "remote hung up");

        var retry = await t.Publish().Handle(new PublishPullRequest(request.JobId), CancellationToken.None);

        Assert.IsTrue(retry.IsSuccess, retry.Error?.ToString());
        var done = t.Jobs.Get(request.JobId);
        Assert.AreEqual(JobState.Done, done.State);
        Assert.IsNull(done.LastError);
        Assert.AreEqual("T (WI-1234)", t.PullRequests.Created.Single().Title, "the stored draft is used on retry");
    }

    [TestMethod]
    public async Task Publishing_fails_after_the_maximum_attempts_with_exponential_backoff()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        t.Worktrees.FailPushes = 10;

        await t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), CancellationToken.None);
        var afterFirst = t.Jobs.Get(request.JobId).NotBefore;
        await t.Publish().Handle(new PublishPullRequest(request.JobId), CancellationToken.None);
        var afterSecond = t.Jobs.Get(request.JobId).NotBefore;
        await t.Publish().Handle(new PublishPullRequest(request.JobId), CancellationToken.None);

        Assert.AreEqual(t.Options.Value.PublishRetryDelay * 2, afterSecond - t.Clock.UtcNow);
        Assert.AreEqual(t.Options.Value.PublishRetryDelay, afterFirst - t.Clock.UtcNow);
        var failed = t.Jobs.Get(request.JobId);
        Assert.AreEqual(JobState.Failed, failed.State);
        StringAssert.Contains(failed.LastError, "after 3 attempts");
    }

    [TestMethod]
    public async Task A_retried_publish_reuses_the_pr_and_does_not_comment_twice()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        await t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), CancellationToken.None);
        var pr = t.Jobs.Get(request.JobId).PullRequest!.Value.Value;

        // The PR exists and the comment was posted, but pretend the job's final save was lost (crash):
        t.Jobs.ForceState(request.JobId, JobState.Publishing);
        t.PullRequests.Existing = new PullRequestRef(77, pr);

        var retry = await t.Publish().Handle(new PublishPullRequest(request.JobId), CancellationToken.None);

        Assert.IsTrue(retry.IsSuccess, retry.Error?.ToString());
        Assert.HasCount(1, t.PullRequests.Created, "no second pull request");
        Assert.AreEqual(1, t.WorkItems.Comments.Count(c => c.Text.Contains("pullrequest/77", StringComparison.Ordinal)), "no second comment");
        Assert.AreEqual(JobState.Done, t.Jobs.Get(request.JobId).State);
    }

    [TestMethod]
    public async Task The_worktree_is_removed_only_after_the_agent_process_exits()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        await t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), CancellationToken.None);
        Assert.IsEmpty(t.Worktrees.Removed, "finish runs inside the agent's turn; its cwd must survive");

        await t.AgentExit().Handle(new HandleAgentExit(request.JobId, new AgentRunOutcome.Exited(0, null)), CancellationToken.None);

        Assert.AreEqual(request.Worktree.Value, t.Worktrees.Removed.Single());
        Assert.AreEqual(JobState.Done, t.Jobs.Get(request.JobId).State);
    }
}
