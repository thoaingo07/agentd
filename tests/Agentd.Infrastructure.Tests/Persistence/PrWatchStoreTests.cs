using Agentd.Application.Monitor;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Persistence.Repositories;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class PrWatchStoreTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");

    [TestMethod]
    public async Task A_watch_round_trips_with_its_bookkeeping_and_a_prepared_fix()
    {
        await using var db = await Database.CreateMigratedAsync("pr_watches_round_trip");
        var store = new PrWatchStore(db);

        var (id, created) = await store.InsertAsync("sysmin", 3944, "Deploy", "tngo", s_discord, "t-1", "guild", [10, 11], default);
        var pending = new PendingFix("/wt/sysmin/watch-1", "c0ffee1", "The build failed in dotnet test; fixed the null check.", [42], new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero));
        await store.UpdateAsync(id, 1, [10, 11, 12], 901, new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), pending, default);

        var w = (await store.GetAsync(id, default))!;
        Assert.IsTrue(created);
        Assert.AreEqual(("sysmin", 3944, "tngo", "t-1", true, 1, 901), (w.Repository, w.PullRequestId, w.WatchedBy, w.ThreadId, w.Active, w.FixRounds, w.LastBuildId));
        CollectionAssert.AreEqual(new[] { 10, 11, 12 }, w.SeenComments.ToList());
        Assert.AreEqual(pending.Commit, w.Pending!.Commit);
        CollectionAssert.AreEqual(new[] { 42 }, w.Pending.Threads.ToList());
        Assert.AreEqual(id, (await store.FindActiveAsync("sysmin", 3944, default))?.Id);
        Assert.AreEqual(id, (await store.FindByThreadAsync(s_discord, "t-1", default))?.Id);
        Assert.AreEqual(id, (await store.ListActiveAsync(default)).Single().Id);
    }

    [TestMethod]
    public async Task Parallel_watches_of_one_pr_make_one_watch_and_stopping_allows_a_new_one()
    {
        await using var db = await Database.CreateMigratedAsync("pr_watches_parallel");
        var store = new PrWatchStore(db);

        // Several people type !watch at once; each opened its own thread before the insert.
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            store.InsertAsync("sysmin", 3944, "Deploy", $"user{i}", s_discord, $"t-{i}", null, [], default))));

        Assert.AreEqual(1, results.Count(r => r.Created), "one of them created it");
        Assert.AreEqual(1, results.Select(r => r.Id).Distinct().Count(), "everyone got the same watch");
        var id = results[0].Id;
        var stopped = await store.StopAsync(id, default);
        var again = await store.StopAsync(id, default);
        var (newId, created) = await store.InsertAsync("sysmin", 3944, "Deploy", "tngo", s_discord, "t-new", null, [], default);

        Assert.AreEqual((true, false), (stopped, again));
        Assert.IsTrue(created && newId != id, "a stopped watch doesn't block watching again");
        Assert.IsFalse((await store.GetAsync(id, default))!.Active);
        Assert.AreEqual(newId, (await store.ListActiveAsync(default)).Single().Id);
    }
}
