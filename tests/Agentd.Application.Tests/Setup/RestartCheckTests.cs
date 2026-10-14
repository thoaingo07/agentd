using Agentd.Application.Ideas;
using Agentd.Application.Setup;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Application.Tests.Setup;

[TestClass]
public sealed class RestartCheckTests
{
    private readonly FakeClock _clock = new();

    [TestMethod]
    public async Task Jobs_preparing_running_or_publishing_count_and_waiting_ones_dont()
    {
        var jobs = new InMemoryJobs(_clock);
        var ids = new List<JobId>();
        foreach (var state in new[] { JobState.Running, JobState.WaitingForHuman, JobState.Publishing, JobState.Queued, JobState.InReview, JobState.Preparing, JobState.Done })
        {
            var job = Job.Create(WorkItemId.From(5700 + ids.Count), RepositoryName.From("sysmin"), "t", _clock);
            await jobs.AddAsync(job, default);
            jobs.ForceState(job.Id, state);
            ids.Add(job.Id);
        }

        var running = await new RestartCheck(jobs, new ThreadTurns()).RunningAsync(default);

        CollectionAssert.AreEqual(new[] { "Job 1 (WI-5700) is running", "Job 3 (WI-5702) is publishing", "Job 6 (WI-5705) is preparing" }, running.Select(r => r.Label).ToArray());
        Assert.IsTrue(running.All(r => r.Source == "job"));
    }

    [TestMethod]
    public async Task A_thread_turn_counts_while_it_runs_and_not_after_even_when_it_throws()
    {
        var turns = new ThreadTurns();
        var agent = new Agent();
        var tracked = new TrackedBrainstormAgent(agent, turns);
        var check = new RestartCheck(new InMemoryJobs(_clock), turns);

        var idea = tracked.RunAsync(Turn(4, ThreadTurnKind.Brainstorm), default);
        var review = tracked.RunAsync(Turn(9, ThreadTurnKind.ReviewAsk), default);
        var running = await check.RunningAsync(default);
        CollectionAssert.AreEqual(new[] { ("idea", 4L, "Idea 4: the agent is answering"), ("review", 9L, "Review 9: the agent is answering a question") },
            running.Select(r => (r.Source, r.Id, r.Label)).ToArray());

        agent.Finish(4);
        agent.Fail(9);
        await idea;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => review);
        Assert.IsEmpty(await check.RunningAsync(default));
    }

    [TestMethod]
    public async Task Parallel_turns_each_count_once_and_all_end()
    {
        var turns = new ThreadTurns();
        var agent = new Agent();
        var tracked = new TrackedBrainstormAgent(agent, turns);

        var runs = Enumerable.Range(1, 20).Select(i => Task.Run(() => tracked.RunAsync(Turn(i, ThreadTurnKind.Chat), default))).ToArray();
        await agent.Started(20);
        CollectionAssert.AreEquivalent(Enumerable.Range(1, 20).Select(i => (long)i).ToArray(), turns.Running.Select(t => t.Id).ToArray());

        agent.FinishAll();
        await Task.WhenAll(runs);
        Assert.IsEmpty(turns.Running);
    }

    [TestMethod]
    public async Task TrackThreadTurns_wraps_whichever_runner_is_registered()
    {
        var agent = new Agent();
        var services = new ServiceCollection().AddSingleton<ThreadTurns>().AddSingleton<IBrainstormAgent>(agent).TrackThreadTurns();
        await using var provider = services.BuildServiceProvider();

        var resolved = provider.GetRequiredService<IBrainstormAgent>();
        var run = resolved.RunAsync(Turn(2, ThreadTurnKind.Chat), default);

        Assert.IsInstanceOfType<TrackedBrainstormAgent>(resolved);
        Assert.AreEqual((ThreadTurnKind.Chat, 2L), provider.GetRequiredService<ThreadTurns>().Running.Single());
        agent.Finish(2);
        await run;
        Assert.HasCount(1, services.Where(d => d.ServiceType == typeof(IBrainstormAgent)));
    }

    private static BrainstormTurn Turn(long id, ThreadTurnKind kind) => new(id, "/wt", Guid.NewGuid(), false, "hi", Kind: kind);

    /// <summary>Each turn runs until the test finishes or fails it.</summary>
    private sealed class Agent : IBrainstormAgent
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TaskCompletionSource<BrainstormReply>> _turns = new();

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken) =>
            _turns.GetOrAdd(turn.IdeaId, _ => new TaskCompletionSource<BrainstormReply>(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        public async Task Started(int count)
        {
            while (_turns.Count < count)
            {
                await Task.Delay(5);
            }
        }

        public void Finish(long id) => _turns[id].SetResult(new BrainstormReply("ok", null, null));

        public void Fail(long id) => _turns[id].SetException(new InvalidOperationException("boom"));

        public void FinishAll()
        {
            foreach (var id in _turns.Keys)
            {
                Finish(id);
            }
        }
    }
}
