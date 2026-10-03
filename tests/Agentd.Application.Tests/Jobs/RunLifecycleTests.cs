using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class RunLifecycleTests
{
    [TestMethod]
    public async Task StartNext_with_nothing_queued_returns_null()
    {
        var t = new TestContext();

        var result = await t.StartNext().Handle(new StartNextJob("w1"), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNull(result.Value);
    }

    [TestMethod]
    public async Task StartNext_creates_the_worktree_and_a_prompt_from_the_work_item()
    {
        var t = new TestContext();

        var request = await t.RunningJobAsync(1234);

        Assert.IsFalse(request.Resume);
        Assert.AreEqual("ai/1234-fix-login", t.Worktrees.Created.Single());
        StringAssert.Contains(request.Prompt, "Work item 1234: Fix login");
        StringAssert.Contains(request.Prompt, "`develop`");
        StringAssert.Contains(request.Prompt, "Call `finish` with a pull request title");
        Assert.AreEqual(JobState.Running, t.Jobs.Get(request.JobId).State);
    }

    [TestMethod]
    public async Task StartNext_fails_the_job_when_preparing_throws()
    {
        var t = new TestContext();
        t.Worktrees.FailCreate = true;
        t.WorkItems.Add(1);
        await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(1)), CancellationToken.None);

        var result = await t.StartNext().Handle(new StartNextJob("w1"), CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        var job = t.Jobs.Single();
        Assert.AreEqual(JobState.Failed, job.State);
        StringAssert.Contains(job.LastError, "git worktree add failed");
    }

    [TestMethod]
    public async Task Agent_exit_without_finish_fails_the_job()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();

        var state = await t.AgentExit().Handle(new HandleAgentExit(request.JobId, new AgentRunOutcome.Exited(0, null)), CancellationToken.None);

        Assert.AreEqual(JobState.Failed, state.Value);
        StringAssert.Contains(t.Jobs.Get(request.JobId).LastError, "without calling finish");
    }

    [TestMethod]
    public async Task Usage_limit_defers_the_job_until_reset_then_resumes_the_same_session()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        var resetAt = t.Clock.UtcNow.AddHours(2);

        var state = await t.AgentExit().Handle(new HandleAgentExit(request.JobId, new AgentRunOutcome.UsageLimited(resetAt)), CancellationToken.None);

        Assert.AreEqual(JobState.Queued, state.Value);
        Assert.AreEqual(resetAt, t.Jobs.Get(request.JobId).NotBefore);
        Assert.IsNull((await t.StartNext().Handle(new StartNextJob("w1"), CancellationToken.None)).Value, "must not start before the reset");

        t.Clock.UtcNow = resetAt.AddMinutes(1);
        var resumed = (await t.StartNext().Handle(new StartNextJob("w1"), CancellationToken.None)).Value!;

        Assert.IsTrue(resumed.Resume);
        Assert.AreEqual(request.Session, resumed.Session);
        Assert.AreEqual(request.Worktree, resumed.Worktree);
        Assert.HasCount(1, t.Worktrees.Created, "the worktree is reused, not recreated");
    }

    [TestMethod]
    public async Task Usage_limit_without_reset_time_uses_the_configured_backoff()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();

        await t.AgentExit().Handle(new HandleAgentExit(request.JobId, new AgentRunOutcome.UsageLimited(null)), CancellationToken.None);

        Assert.AreEqual(t.Clock.UtcNow + t.Options.Value.UsageLimitBackoff, t.Jobs.Get(request.JobId).NotBefore);
    }

    [TestMethod]
    public async Task Finish_pushes_opens_the_pr_completes_and_comments()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();

        var pr = await t.Finish().Handle(new FinishWork(request.JobId, "Fix login redirect", "Details", "Fixed it"), CancellationToken.None);

        Assert.IsTrue(pr.IsSuccess, pr.Error?.ToString());
        Assert.AreEqual(("ai/1234-fix-login", "develop", "Fix login redirect (WI-1234)"), t.PullRequests.Created.Single());
        Assert.AreEqual("ai/1234-fix-login", t.Worktrees.Pushed.Single());
        var job = t.Jobs.Get(request.JobId);
        Assert.AreEqual(JobState.Done, job.State);
        Assert.AreEqual(pr.Value!.Url, job.PullRequest!.Value.Value);
        Assert.IsTrue(t.WorkItems.Comments.Any(c => c.Text.Contains("pullrequest/77", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Finish_reuses_an_existing_open_pr()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        t.PullRequests.Existing = new PullRequestRef(5, new Uri("https://dev.azure.com/ermsystem/Portal/_git/sysmin/pullrequest/5"));

        var pr = await t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), CancellationToken.None);

        Assert.AreEqual(5, pr.Value!.Id);
        Assert.IsEmpty(t.PullRequests.Created);
    }

    [TestMethod]
    public async Task Finish_without_commits_fails_the_job()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        t.Worktrees.HasCommits = false;

        var pr = await t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), CancellationToken.None);

        Assert.IsFalse(pr.IsSuccess);
        Assert.AreEqual(JobState.Failed, t.Jobs.Get(request.JobId).State);
        Assert.IsEmpty(t.Worktrees.Pushed);
    }

    [TestMethod]
    public async Task Finish_on_a_queued_job_is_an_invalid_transition()
    {
        var t = new TestContext();
        t.WorkItems.Add(1);
        var id = (await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(1)), CancellationToken.None)).Value;

        var result = await t.Finish().Handle(new FinishWork(id, "T", "D", "S"), CancellationToken.None);

        Assert.AreEqual("invalid_transition", result.Error?.Code);
    }

    [TestMethod]
    public async Task Finish_with_an_empty_title_is_a_validation_error()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();

        var result = await t.Finish().Handle(new FinishWork(request.JobId, " ", "D", "S"), CancellationToken.None);

        Assert.AreEqual("validation", result.Error?.Code);
        Assert.AreEqual(JobState.Running, t.Jobs.Get(request.JobId).State);
    }

    [TestMethod]
    public async Task Cancel_stops_the_runner_and_cancels_the_job()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        t.Runner.Running.Add(request.JobId.Value);

        var result = await t.Cancel().Handle(new CancelJob(request.JobId, "tngo"), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(request.JobId.Value, t.Runner.CancelledJobs.Single());
        Assert.AreEqual(JobState.Cancelled, t.Jobs.Get(request.JobId).State);
    }

    [TestMethod]
    public async Task Status_lists_active_jobs_with_elapsed_time()
    {
        var t = new TestContext();
        await t.RunningJobAsync();
        t.Clock.UtcNow = t.Clock.UtcNow.AddMinutes(12);

        var rows = await new GetJobStatusHandler(t.Jobs, t.Clock).Handle(new GetJobStatus(), CancellationToken.None);

        var row = rows.Single();
        Assert.AreEqual(JobState.Running, row.State);
        Assert.AreEqual(TimeSpan.FromMinutes(12), row.Elapsed);
    }
}
