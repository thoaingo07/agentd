using Agentd.Application.Incidents;
using Agentd.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class IncidentStoreTests
{
    private static readonly DateTimeOffset s_t0 = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);
    private static NpgsqlDataSource s_db = null!;
    private static int s_next;

    [ClassInitialize]
    public static async Task InitAsync(TestContext _) => s_db = await Database.CreateMigratedAsync("incidents");

    [ClassCleanup]
    public static async Task CleanupAsync() => await s_db.DisposeAsync();

    [TestMethod]
    public async Task A_group_becomes_an_incident_at_the_threshold_then_always_counts_and_reads_back_whole()
    {
        var store = new IncidentStore(s_db);
        var app = App();

        Assert.IsNull(await store.RecordAsync(app, Group("ex|p1|GET /orders", 2), 3, default), "under the threshold: not an incident");
        var created = await store.RecordAsync(app, Group("ex|p1|GET /orders", 4), 3, default);
        var again = await store.RecordAsync(app, Group("ex|p1|GET /orders", 1, s_t0.AddMinutes(-30), s_t0.AddMinutes(20), "newer message"), 3, default);

        Assert.AreEqual((true, false), (created!.Created, created.Reopened));
        Assert.AreEqual((created.Id, false, false), (again!.Id, again.Created, again.Reopened));
        var incident = (await store.GetAsync(created.Id, default))!;
        Assert.AreEqual((app, "ex|p1|GET /orders", ErrorKinds.Exception, "NullReferenceException", "newer message", "GET /orders"),
            (incident.App, incident.Fingerprint, incident.Kind, incident.Title, incident.Message, incident.Operation));
        Assert.AreEqual((IncidentStatus.Open, 5L, s_t0.AddMinutes(-30), s_t0.AddMinutes(20)), (incident.Status, incident.TotalCount, incident.FirstSeen, incident.LastSeen));
        Assert.AreEqual(new ErrorSample("at Orders.Get()", "500", "op-1"), incident.Sample);
        Assert.IsNull(incident.ThreadId);
    }

    [TestMethod]
    public async Task A_resolved_incident_that_recurs_reopens_and_an_ignored_one_stays_ignored()
    {
        var store = new IncidentStore(s_db);
        var app = App();
        var resolved = (await store.RecordAsync(app, Group("ex|r|op", 5), 3, default))!.Id;
        var ignored = (await store.RecordAsync(app, Group("ex|i|op", 5), 3, default))!.Id;
        await ExecuteAsync($"UPDATE agentd.incidents SET status = 'Resolved' WHERE id = {resolved}; UPDATE agentd.incidents SET status = 'Ignored' WHERE id = {ignored}");

        var back = await store.RecordAsync(app, Group("ex|r|op", 1), 3, default);
        var still = await store.RecordAsync(app, Group("ex|i|op", 9), 3, default);

        Assert.AreEqual((false, true), (back!.Created, back.Reopened));
        Assert.AreEqual(IncidentStatus.Open, (await store.GetAsync(resolved, default))!.Status);
        Assert.IsFalse(still!.Reopened);
        Assert.AreEqual(IncidentStatus.Ignored, (await store.GetAsync(ignored, default))!.Status);
    }

    [TestMethod]
    public async Task Parallel_callers_on_one_fingerprint_make_one_incident_with_every_count_and_one_creator()
    {
        var store = new IncidentStore(s_db);
        var app = App();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => store.RecordAsync(app, Group("req|GET /x|500", 3, kind: ErrorKinds.Request), 3, default))));

        Assert.AreEqual(1, results.Select(r => r!.Id).Distinct().Count());
        Assert.AreEqual(1, results.Count(r => r!.Created));
        Assert.AreEqual(60L, (await store.GetAsync(results[0]!.Id, default))!.TotalCount);
    }

    [TestMethod]
    public async Task The_list_is_newest_first_by_status_and_the_thread_is_kept()
    {
        var store = new IncidentStore(s_db);
        var app = App();
        var older = (await store.RecordAsync(app, Group("ex|old|op", 5, s_t0.AddDays(-2), s_t0.AddDays(-2)), 3, default))!.Id;
        var newer = (await store.RecordAsync(app, Group("ex|new|op", 5, s_t0.AddDays(5), s_t0.AddDays(5)), 3, default))!.Id;
        await store.SetThreadAsync(newer, "discord", "t-1", "g-1", default);
        await ExecuteAsync($"UPDATE agentd.incidents SET status = 'Resolved' WHERE id = {older}");

        var all = (await store.ListAsync(null, 500, default)).Where(i => i.App == app).Select(i => i.Id).ToArray();
        var open = (await store.ListAsync(IncidentStatus.Open, 500, default)).Where(i => i.App == app).Select(i => i.Id).ToArray();

        CollectionAssert.AreEqual(new[] { newer, older }, all);
        CollectionAssert.AreEqual(new[] { newer }, open);
        var thread = (await store.GetAsync(newer, default))!;
        Assert.AreEqual(("discord", "t-1", "g-1"), (thread.Provider, thread.ThreadId, thread.SpaceId));
        Assert.HasCount(1, await store.ListAsync(null, 1, default));
    }

    [TestMethod]
    public async Task The_read_position_only_moves_forward_even_from_parallel_callers()
    {
        var store = new IncidentStore(s_db);
        var app = App();
        Assert.IsNull(await store.GetCursorAsync(app, default));

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => store.SetCursorAsync(app, s_t0.AddMinutes(i), default)));
        await store.SetCursorAsync(app, s_t0, default);

        Assert.AreEqual(s_t0.AddMinutes(9), await store.GetCursorAsync(app, default));
    }

    private static string App() => $"app-{Interlocked.Increment(ref s_next)}";

    private static ErrorGroup Group(string fingerprint, long count, DateTimeOffset? first = null, DateTimeOffset? last = null, string message = "Object reference not set",
        string kind = ErrorKinds.Exception) =>
        new(fingerprint, kind, "NullReferenceException", message, fingerprint.Split('|')[^1], "orders", count, 2, first ?? s_t0, last ?? s_t0.AddMinutes(5),
            new ErrorSample("at Orders.Get()", "500", "op-1"));

    private static async Task ExecuteAsync(string sql)
    {
        await using var cmd = s_db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
