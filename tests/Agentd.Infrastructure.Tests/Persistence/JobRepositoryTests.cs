using System.Collections.Concurrent;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class JobRepositoryTests
{
    private static NpgsqlDataSource s_db = null!;
    private static int s_nextWorkItem = 1000;
    private readonly TestClock _clock = new();

    [ClassInitialize]
    public static async Task InitAsync(TestContext _) => s_db = await Database.CreateMigratedAsync("job_repository");

    [ClassCleanup]
    public static async Task CleanupAsync() => await s_db.DisposeAsync();

    private JobRepository Repo() => new(s_db, _clock);

    [TestMethod]
    public async Task A_job_round_trips_through_its_whole_lifecycle_with_increasing_versions()
    {
        var repo = Repo();
        var job = NewJob();
        Assert.IsTrue((await repo.AddAsync(job, default)).IsSuccess);
        Assert.AreEqual(1L, job.Version);

        var dequeued = (await repo.DequeueNextAsyncFor(job.Id))!;
        Assert.AreEqual(JobState.Preparing, dequeued.State);

        dequeued.Start(new WorktreePath("/wt/1"), BranchName.From("ai/1-x"), ClaudeSessionId.New());
        Assert.IsTrue((await repo.SaveAsync(dequeued, default)).IsSuccess);
        dequeued.Finish(PullRequestDraft.Create("Fix it", "desc", "sum").Value!);
        Assert.IsTrue((await repo.SaveAsync(dequeued, default)).IsSuccess);
        dequeued.Complete(new PullRequestUrl(new Uri("https://dev.azure.com/o/p/_git/r/pullrequest/9")));
        Assert.IsTrue((await repo.SaveAsync(dequeued, default)).IsSuccess);

        var reloaded = (await repo.GetAsync(job.Id, default))!;
        Assert.AreEqual(JobState.Done, reloaded.State);
        Assert.AreEqual("Fix it", reloaded.Draft!.Title);
        Assert.AreEqual(dequeued.Session, reloaded.Session);
        Assert.AreEqual(5L, reloaded.Version);   // insert=1, dequeue=2, start=3, finish=4, complete=5
    }

    [TestMethod]
    public async Task Domain_events_are_stored_with_the_state_change()
    {
        var repo = Repo();
        var job = NewJob();
        await repo.AddAsync(job, default);

        var types = await EventTypesAsync(job.Id);

        CollectionAssert.AreEqual(new List<string> { "JobQueued" }, types);
    }

    [TestMethod]
    public async Task A_second_active_job_for_the_same_work_item_is_a_conflict()
    {
        var repo = Repo();
        var wi = Interlocked.Increment(ref s_nextWorkItem);
        await repo.AddAsync(NewJob(wi), default);

        var second = await repo.AddAsync(NewJob(wi), default);

        Assert.AreEqual("conflict", second.Error?.Code);
    }

    [TestMethod]
    public async Task A_stale_version_is_a_conflict_and_changes_nothing()
    {
        var repo = Repo();
        var job = NewJob();
        await repo.AddAsync(job, default);
        var copyA = (await repo.GetAsync(job.Id, default))!;
        var copyB = (await repo.GetAsync(job.Id, default))!;

        copyA.Cancel("a");
        Assert.IsTrue((await repo.SaveAsync(copyA, default)).IsSuccess);
        copyB.Fail("b");
        var stale = await repo.SaveAsync(copyB, default);

        Assert.AreEqual("conflict", stale.Error?.Code);
        Assert.AreEqual(JobState.Cancelled, (await repo.GetAsync(job.Id, default))!.State);
        CollectionAssert.DoesNotContain(await EventTypesAsync(job.Id), "JobFailed");
    }

    [TestMethod]
    public async Task Parallel_workers_dequeue_distinct_jobs()
    {
        var repo = Repo();
        var ids = new List<JobId>();
        for (var i = 0; i < 20; i++)
        {
            var job = NewJob();
            await repo.AddAsync(job, default);
            ids.Add(job.Id);
        }

        var taken = new ConcurrentBag<long>();
        await Parallel.ForEachAsync(Enumerable.Range(0, 30), async (w, ct) =>
        {
            // More workers than jobs; each keeps dequeuing until nothing is left.
            while (await Repo().DequeueNextAsync($"w{w}", ct) is { } job)
            {
                taken.Add(job.Id.Value);
            }
        });

        var ours = taken.Where(id => ids.Any(j => j.Value == id)).ToList();
        Assert.HasCount(20, ours, "every job taken exactly once");
        Assert.HasCount(20, ours.Distinct());
    }

    [TestMethod]
    public async Task Deferred_jobs_are_not_dequeued_before_not_before()
    {
        var repo = Repo();
        var job = NewJob();
        await repo.AddAsync(job, default);
        var running = (await repo.DequeueNextAsyncFor(job.Id))!;
        running.Start(new WorktreePath("/wt/d"), BranchName.From("ai/d"), ClaudeSessionId.New());
        await repo.SaveAsync(running, default);
        running.Defer(_clock.UtcNow.AddHours(1), "usage limit");
        await repo.SaveAsync(running, default);

        Assert.IsNull(await repo.DequeueNextAsyncFor(job.Id), "must wait until not_before");

        _clock.UtcNow = _clock.UtcNow.AddHours(2);
        var resumed = await repo.DequeueNextAsyncFor(job.Id);
        Assert.IsNotNull(resumed);
        Assert.AreEqual(running.Session, resumed.Session);
    }

    [TestMethod]
    public async Task Lists_by_state_and_active_lookup()
    {
        var repo = Repo();
        var job = NewJob();
        await repo.AddAsync(job, default);

        var queued = await repo.ListByStateAsync([JobState.Queued], default);
        var active = await repo.FindActiveByWorkItemAsync(job.WorkItemId, default);

        Assert.IsTrue(queued.Any(j => j.Id == job.Id));
        Assert.AreEqual(job.Id, active!.Id);
    }

    [TestMethod]
    public async Task Waiting_for_a_human_and_queued_replies_round_trip()
    {
        var repo = Repo();
        var job = NewJob();
        await repo.AddAsync(job, default);
        var running = (await repo.DequeueNextAsyncFor(job.Id))!;
        running.Start(new WorktreePath("/wt/2"), BranchName.From("ai/2-x"), ClaudeSessionId.New());
        running.ResumeWith("also update the docs", "tngo");
        await repo.SaveAsync(running, default);
        running.AskDeveloper("Which endpoint?", ["v1", "v2"]);
        Assert.IsTrue((await repo.SaveAsync(running, default)).IsSuccess);

        var loaded = (await repo.GetAsync(job.Id, default))!;

        Assert.AreEqual(JobState.WaitingForHuman, loaded.State);
        CollectionAssert.AreEqual(new[] { "also update the docs" }, loaded.PendingMessages.ToArray());
        Assert.AreEqual(loaded.Id, (await repo.FindActiveByWorkItemAsync(loaded.WorkItemId, default))?.Id, "waiting jobs stay active");
        CollectionAssert.IsSubsetOf(new[] { "DeveloperReplied", "DeveloperQuestionAsked" }, await EventTypesAsync(job.Id));
    }

    private Job NewJob(int? workItem = null) =>
        Job.Create(WorkItemId.From(workItem ?? Interlocked.Increment(ref s_nextWorkItem)), RepositoryName.From("sysmin"), "Fix login", _clock);

    private static async Task<List<string>> EventTypesAsync(JobId id)
    {
        await using var cmd = s_db.CreateCommand("SELECT type FROM agentd.events WHERE job_id = $1 ORDER BY seq");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id.Value });
        await using var reader = await cmd.ExecuteReaderAsync();
        var types = new List<string>();
        while (await reader.ReadAsync())
        {
            types.Add(reader.GetString(0));
        }

        return types;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }
}

internal static class JobRepositoryTestExtensions
{
    /// <summary>Dequeues until the given job comes up (other test classes' jobs live in other databases).</summary>
    public static async Task<Job?> DequeueNextAsyncFor(this JobRepository repo, JobId id)
    {
        while (await repo.DequeueNextAsync("test", default) is { } job)
        {
            if (job.Id == id)
            {
                return job;
            }
        }

        return null;
    }
}
