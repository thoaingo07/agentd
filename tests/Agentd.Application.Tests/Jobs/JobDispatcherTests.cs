using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class JobDispatcherTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Starts_at_most_MaxConcurrent_agents()
    {
        var (t, dispatcher, provider) = await Create(queued: 3, maxConcurrent: 2);
        using var _ = dispatcher;
        await using var __ = provider;

        Assert.IsTrue(await dispatcher.TryStartNextAsync(CancellationToken.None));
        Assert.IsTrue(await dispatcher.TryStartNextAsync(CancellationToken.None));
        var third = dispatcher.TryStartNextAsync(CancellationToken.None);
        await WaitUntil(() => t.Runner.Started.Count == 2);

        Assert.IsFalse(third.IsCompleted, "the third job waits for a free slot");
        Assert.AreEqual(2, dispatcher.ActiveCount);
        Assert.AreEqual(1, t.Jobs.ListByStateAsync([JobState.Queued], CancellationToken.None).Result.Count);

        // The first agent exits: its slot frees up and the third job starts.
        t.Runner.Release(t.Runner.Started[0].JobId, new AgentRunOutcome.Exited(1, null));
        Assert.IsTrue(await third.WaitAsync(s_timeout));
        await WaitUntil(() => t.Runner.Started.Count == 3);
        Assert.AreEqual(JobState.Failed, t.Jobs.Get(t.Runner.Started[0].JobId).State, "exit without finish → failed");
    }

    [TestMethod]
    public async Task Nothing_queued_returns_false_and_frees_the_slot()
    {
        var (_, dispatcher, provider) = await Create(queued: 0, maxConcurrent: 1);
        using var _ = dispatcher;
        await using var __ = provider;

        Assert.IsFalse(await dispatcher.TryStartNextAsync(CancellationToken.None));
        Assert.IsFalse(await dispatcher.TryStartNextAsync(CancellationToken.None).WaitAsync(s_timeout));
    }

    [TestMethod]
    public async Task Shutdown_kills_agents_but_leaves_their_jobs_running_for_recovery()
    {
        var (t, dispatcher, provider) = await Create(queued: 1, maxConcurrent: 2);
        using var _ = dispatcher;
        await using var __ = provider;
        await dispatcher.TryStartNextAsync(CancellationToken.None);
        await WaitUntil(() => t.Runner.Started.Count == 1);

        await dispatcher.StopAsync().WaitAsync(s_timeout);

        Assert.AreEqual(0, dispatcher.ActiveCount);
        Assert.AreEqual(JobState.Running, t.Jobs.Get(t.Runner.Started[0].JobId).State);
        Assert.IsFalse(await dispatcher.TryStartNextAsync(CancellationToken.None), "no new jobs after stop");
    }

    [TestMethod]
    public async Task Resume_requests_run_through_the_same_slots()
    {
        var (t, dispatcher, provider) = await Create(queued: 0, maxConcurrent: 1);
        using var _ = dispatcher;
        await using var __ = provider;
        var running = await t.RunningJobAsync(99);
        var plan = (await t.Recover().Handle(new RecoverJobsOnStartup(), CancellationToken.None)).Value!;

        await dispatcher.ResumeAsync(plan.Resume.Single(), CancellationToken.None);
        await WaitUntil(() => t.Runner.Started.Count == 1);

        Assert.IsTrue(t.Runner.Started[0].Resume);
        Assert.AreEqual(running.Session, t.Runner.Started[0].Session);
        Assert.IsFalse(dispatcher.TryStartNextAsync(CancellationToken.None).IsCompleted, "the only slot is taken");
    }

    private static async Task<(TestContext, JobDispatcher, ServiceProvider)> Create(int queued, int maxConcurrent)
    {
        var t = new TestContext();
        t.Runner.Hold = true;
        for (var i = 1; i <= queued; i++)
        {
            t.WorkItems.Add(1000 + i);
            await t.Claim().Handle(new ClaimWorkItem(Domain.Jobs.ValueObjects.WorkItemId.From(1000 + i)), CancellationToken.None);
        }

        var provider = new ServiceCollection()
            .AddSingleton<ICommandHandler<ResumeJobTurn, AgentRunRequest?>>(new ResumeJobTurnHandler(t.Jobs))
            .AddSingleton<ICommandHandler<StartNextJob, AgentRunRequest?>>(t.StartNext())
            .AddSingleton<ICommandHandler<HandleAgentExit, JobState>>(t.AgentExit())
            .BuildServiceProvider();
        var options = Microsoft.Extensions.Options.Options.Create(new SchedulerOptions { MaxConcurrent = maxConcurrent, ShutdownGrace = TimeSpan.FromMilliseconds(50) });
        var dispatcher = new JobDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), t.Runner, options, NullLogger<JobDispatcher>.Instance);
        return (t, dispatcher, provider);
    }

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
