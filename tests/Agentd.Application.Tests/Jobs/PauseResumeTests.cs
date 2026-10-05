using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.Events;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class PauseResumeTests
{
    [TestMethod]
    public async Task Pausing_stops_the_agent_and_keeps_its_session_and_worktree()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        t.Runner.Running.Add(request.JobId.Value);

        Assert.IsTrue((await new PauseJobHandler(t.Jobs, t.Runner).Handle(new PauseJob(request.JobId, "tngo"), default)).IsSuccess);
        Assert.AreEqual(JobState.Paused, (await t.AgentExit().Handle(new HandleAgentExit(request.JobId, new AgentRunOutcome.Cancelled()), default)).Value, "the stopped agent's exit doesn't cancel the job");

        CollectionAssert.Contains(t.Runner.CancelledJobs, request.JobId.Value);
        Assert.IsEmpty(t.Worktrees.Removed, "the worktree stays for the resume");
        var job = t.Jobs.Get(request.JobId);
        Assert.AreEqual((JobState.Paused, request.Session), (job.State, job.Session));
        Assert.AreEqual("tngo", t.Jobs.SavedEvents.OfType<JobPaused>().Single().By);
    }

    [TestMethod]
    public async Task Resuming_continues_the_same_session_in_the_same_worktree()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        await new PauseJobHandler(t.Jobs, t.Runner).Handle(new PauseJob(request.JobId, "tngo"), default);

        Assert.IsTrue((await new ResumeJobHandler(t.Jobs).Handle(new ResumeJob(request.JobId, "tngo"), default)).IsSuccess);
        var resumed = (await t.StartNext().Handle(new StartNextJob("w1"), default)).Value!;

        Assert.AreEqual((request.Session, request.Worktree, true), (resumed.Session, resumed.Worktree, resumed.Resume));
        Assert.AreEqual(JobState.Running, t.Jobs.Get(request.JobId).State);
    }

    [TestMethod]
    public async Task A_cancelled_job_can_be_retried_and_resumes_on_its_branch()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        await t.Cancel().Handle(new CancelJob(request.JobId, "tngo"), default);
        await t.AgentExit().Handle(new HandleAgentExit(request.JobId, new AgentRunOutcome.Cancelled()), default);
        CollectionAssert.Contains(t.Worktrees.Removed, request.Worktree.Value, "cancelling removed the worktree");

        Assert.AreEqual(2, (await new RetryJobHandler(t.Jobs).Handle(new RetryJob(request.JobId), default)).Value);
        var retried = (await t.StartNext().Handle(new StartNextJob("w1"), default)).Value!;

        Assert.AreEqual((request.Session, true), (retried.Session, retried.Resume), "the session resumes");
        Assert.AreEqual(request.Worktree, retried.Worktree, "recreated at the same path from the kept branch");
        Assert.AreEqual(2, t.Worktrees.Created.Count(b => b == t.Jobs.Get(request.JobId).Branch!.Value.Value));
    }

    [TestMethod]
    public async Task A_finished_job_cannot_be_paused()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        await t.Cancel().Handle(new CancelJob(request.JobId, "tngo"), default);

        Assert.AreEqual("invalid_transition", (await new PauseJobHandler(t.Jobs, t.Runner).Handle(new PauseJob(request.JobId, "tngo"), default)).Error!.Code);
    }
}
