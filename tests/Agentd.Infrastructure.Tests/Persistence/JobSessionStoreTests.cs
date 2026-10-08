using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class JobSessionStoreTests
{
    [TestMethod]
    public async Task A_profile_session_is_created_once_and_then_reused()
    {
        await using var db = await Database.CreateMigratedAsync("job_sessions_reuse");
        var job = await AddJobAsync(db, 5613);
        var store = new JobSessionStore(db);
        var first = Guid.NewGuid();

        var created = await store.GetOrCreateAsync(job, "deepseek", first, default);
        var again = await store.GetOrCreateAsync(job, "deepseek", Guid.NewGuid(), default);
        var other = await store.GetOrCreateAsync(job, "glm", Guid.NewGuid(), default);

        Assert.AreEqual((first, true), created);
        Assert.AreEqual((first, false), again, "the same session, not the new proposal");
        Assert.IsTrue(other.Created);
        Assert.AreNotEqual(first, other.Session, "each profile has its own session");
    }

    [TestMethod]
    public async Task Parallel_callers_all_get_the_one_session_and_only_one_created_it()
    {
        await using var db = await Database.CreateMigratedAsync("job_sessions_parallel");
        var job = await AddJobAsync(db, 5614);
        var store = new JobSessionStore(db);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.GetOrCreateAsync(job, "deepseek", Guid.NewGuid(), default))));

        Assert.AreEqual(1, results.Select(r => r.Session).Distinct().Count());
        Assert.AreEqual(1, results.Count(r => r.Created));
    }

    [TestMethod]
    public async Task The_latest_plan_is_kept()
    {
        await using var db = await Database.CreateMigratedAsync("job_plans");
        var job = await AddJobAsync(db, 5615);
        var store = new JobSessionStore(db);

        var none = await store.GetAsync(job, default);
        await store.SaveAsync(job, "1. Edit the README", DateTimeOffset.UtcNow, default);
        await store.SaveAsync(job, "1. Edit the README\n2. Run the tests", DateTimeOffset.UtcNow, default);

        Assert.IsNull(none);
        Assert.AreEqual("1. Edit the README\n2. Run the tests", await store.GetAsync(job, default), "a resubmitted plan replaces it");
    }

    private static async Task<JobId> AddJobAsync(NpgsqlDataSource db, int workItem)
    {
        var clock = new FixedClock();
        var repo = new JobRepository(db, clock);
        var job = Job.Create(WorkItemId.From(workItem), RepositoryName.From("sysmin"), "Fix login", clock);
        Assert.IsTrue((await repo.AddAsync(job, default)).IsSuccess);
        return job.Id;
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }
}
