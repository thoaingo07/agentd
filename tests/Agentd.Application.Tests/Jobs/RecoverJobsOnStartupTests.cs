using Agentd.Application.Jobs;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class RecoverJobsOnStartupTests
{
    [TestMethod]
    public async Task Orphaned_running_job_resumes_in_the_same_session()
    {
        var t = new TestContext();
        var running = await t.RunningJobAsync();

        var plan = (await t.Recover().Handle(new RecoverJobsOnStartup(), CancellationToken.None)).Value!;

        var resume = plan.Resume.Single();
        Assert.AreEqual(running.JobId, resume.JobId);
        Assert.IsTrue(resume.Resume, "resume runs use claude --resume");
        Assert.AreEqual(running.Session, resume.Session);
        Assert.AreEqual(running.Worktree, resume.Worktree);
        Assert.AreEqual(TaskPromptBuilder.ResumePrompt, resume.Prompt);
        var job = t.Jobs.Get(running.JobId);
        Assert.AreEqual(JobState.Running, job.State);
        Assert.AreEqual(1, job.ResumeCount);
    }

    [TestMethod]
    public async Task Running_job_whose_process_is_alive_is_left_alone()
    {
        var t = new TestContext();
        var running = await t.RunningJobAsync();
        t.Runner.Running.Add(running.JobId.Value);

        var plan = (await t.Recover().Handle(new RecoverJobsOnStartup(), CancellationToken.None)).Value!;

        Assert.IsEmpty(plan.Resume);
        Assert.AreEqual(0, t.Jobs.Get(running.JobId).ResumeCount);
    }

    [TestMethod]
    public async Task Preparing_job_goes_back_to_the_queue_and_starts_again()
    {
        var t = new TestContext();
        t.WorkItems.Add(1234);
        var id = (await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(1234)), CancellationToken.None)).Value;
        _ = await t.Jobs.DequeueNextAsync("w1", CancellationToken.None);   // crashed right after the dequeue
        Assert.AreEqual(JobState.Preparing, t.Jobs.Get(id).State);

        var plan = (await t.Recover().Handle(new RecoverJobsOnStartup(), CancellationToken.None)).Value!;

        Assert.AreEqual(1, plan.RequeuedCount);
        Assert.AreEqual(JobState.Queued, t.Jobs.Get(id).State);
        var request = (await t.StartNext().Handle(new StartNextJob("w1"), CancellationToken.None)).Value;
        Assert.AreEqual(id, request!.JobId);
    }

    [TestMethod]
    public async Task Publishing_job_is_published_again()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        var job = t.Jobs.Get(request.JobId);
        job.Finish(PullRequestDraft.Create("T", "D", "S").Value!);
        await t.Jobs.SaveAsync(job, CancellationToken.None);

        var plan = (await t.Recover().Handle(new RecoverJobsOnStartup(), CancellationToken.None)).Value!;

        Assert.AreEqual(1, plan.RepublishedCount);
        Assert.AreEqual(JobState.Done, t.Jobs.Get(request.JobId).State);
    }

    [TestMethod]
    public async Task Publishing_job_waiting_for_a_retry_is_left_to_the_scheduler()
    {
        var t = new TestContext();
        var request = await t.PublishFailedJobAsync();

        var plan = (await t.Recover().Handle(new RecoverJobsOnStartup(), CancellationToken.None)).Value!;

        Assert.AreEqual(0, plan.RepublishedCount);
        Assert.AreEqual(JobState.Publishing, t.Jobs.Get(request.JobId).State);
    }

    [TestMethod]
    public async Task Worktrees_are_pruned_for_every_repository()
    {
        var t = new TestContext();

        await t.Recover().Handle(new RecoverJobsOnStartup(), CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "sysmin" }, t.Worktrees.Pruned);
    }

    [TestMethod]
    public async Task Due_publish_retries_run_and_later_ones_wait()
    {
        var t = new TestContext();
        var request = await t.PublishFailedJobAsync();

        Assert.AreEqual(0, (await t.RetryPublishes().Handle(new RetryDuePublishes(), CancellationToken.None)).Value, "not due yet");

        t.Clock.UtcNow = t.Clock.UtcNow.AddMinutes(5);
        Assert.AreEqual(1, (await t.RetryPublishes().Handle(new RetryDuePublishes(), CancellationToken.None)).Value);
        Assert.AreEqual(JobState.Done, t.Jobs.Get(request.JobId).State);
    }
}
