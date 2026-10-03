using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class AskDeveloperFlowTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Ask_wait_reply_resumes_the_same_session_with_the_answer()
    {
        var t = new TestContext();
        t.Runner.Hold = true;
        t.WorkItems.Add(1234);
        await t.Claim().Handle(new ClaimWorkItem(Domain.Jobs.ValueObjects.WorkItemId.From(1234)), default);
        await using var provider = Services(t);
        using var dispatcher = Dispatcher(t, provider);

        // First turn: the agent asks, then its process exits.
        Assert.IsTrue(await dispatcher.TryStartNextAsync(default));
        await WaitUntil(() => t.Runner.Started.Count == 1);
        var first = t.Runner.Started[0];
        Assert.IsTrue((await new AskDeveloperHandler(t.Jobs).Handle(new AskDeveloper(first.JobId, "v1 or v2?", ["v1", "v2"]), default)).IsSuccess);
        t.Runner.Release(first.JobId, new AgentRunOutcome.Exited(0, new AgentResultSummary(3, null, false)));
        await WaitUntil(() => dispatcher.ActiveCount == 0);
        Assert.AreEqual(JobState.WaitingForHuman, t.Jobs.Get(first.JobId).State, "waiting, with no process");
        Assert.IsFalse(await dispatcher.TryStartNextAsync(default), "nothing to run while waiting");

        // The developer answers: a resume turn in the same session carries the answer.
        Assert.AreEqual(DeveloperMessageOutcome.Resumed, (await new SubmitDeveloperMessageHandler(t.Jobs).Handle(new SubmitDeveloperMessage(first.JobId, "v2 please", "tngo"), default)).Value);
        Assert.IsTrue(await dispatcher.TryStartNextAsync(default));
        await WaitUntil(() => t.Runner.Started.Count == 2);

        var resume = t.Runner.Started[1];
        Assert.IsTrue(resume.Resume);
        Assert.AreEqual(first.Session, resume.Session);
        Assert.AreEqual(first.Worktree, resume.Worktree);
        StringAssert.Contains(resume.Prompt, "- tngo: v2 please");
        Assert.IsEmpty(t.Jobs.Get(first.JobId).PendingMessages, "the replies were handed to the turn");
    }

    [TestMethod]
    public async Task Replies_sent_during_a_turn_start_another_turn_after_it_ends()
    {
        var t = new TestContext();
        t.Runner.Hold = true;
        t.WorkItems.Add(1234);
        await t.Claim().Handle(new ClaimWorkItem(Domain.Jobs.ValueObjects.WorkItemId.From(1234)), default);
        await using var provider = Services(t);
        using var dispatcher = Dispatcher(t, provider);
        await dispatcher.TryStartNextAsync(default);
        await WaitUntil(() => t.Runner.Started.Count == 1);
        var job = t.Runner.Started[0].JobId;

        await new SubmitDeveloperMessageHandler(t.Jobs).Handle(new SubmitDeveloperMessage(job, "also update the docs", "alice"), default);
        Assert.IsFalse(await dispatcher.TryStartNextAsync(default), "never a second process for a busy job");

        t.Runner.Release(job, new AgentRunOutcome.Exited(0, new AgentResultSummary(5, null, false)));
        await WaitUntil(() => dispatcher.ActiveCount == 0);
        Assert.AreEqual(JobState.Running, t.Jobs.Get(job).State, "exiting with queued replies is not a failure");
        Assert.IsTrue(await dispatcher.TryStartNextAsync(default));
        await WaitUntil(() => t.Runner.Started.Count == 2);
        StringAssert.Contains(t.Runner.Started[1].Prompt, "- alice: also update the docs");
    }

    private static ServiceProvider Services(TestContext t) =>
        new ServiceCollection()
            .AddSingleton<ICommandHandler<ResumeJobTurn, AgentRunRequest?>>(new ResumeJobTurnHandler(t.Jobs, t.Outbox))
            .AddSingleton<ICommandHandler<StartNextJob, AgentRunRequest?>>(t.StartNext())
            .AddSingleton<ICommandHandler<HandleAgentExit, JobState>>(t.AgentExit())
            .BuildServiceProvider();

    private static JobDispatcher Dispatcher(TestContext t, ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), t.Runner,
            Microsoft.Extensions.Options.Options.Create(new SchedulerOptions { MaxConcurrent = 2 }), NullLogger<JobDispatcher>.Instance);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_timeout;
        while (!condition())
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "timed out waiting for the condition");
            await Task.Delay(10);
        }
    }
}
