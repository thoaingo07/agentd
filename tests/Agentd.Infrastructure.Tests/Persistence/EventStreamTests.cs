using Agentd.Application.Events;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Persistence.Events;
using Agentd.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class EventStreamTests
{
    private static int s_next;

    [TestMethod]
    public async Task Paging_works_in_both_directions_and_the_all_stream_is_summary_only()
    {
        await using var db = await Database.CreateMigratedAsync(Name());
        var store = new EventStore(db);
        var jobs = new[] { await JobAsync(db), await JobAsync(db), await JobAsync(db) };
        for (var i = 0; i < 300; i++)
        {
            foreach (var job in jobs)
            {
                await store.AppendAsync(job, i % 10 == 0 ? "phase.set" : "agent.text", $$"""{"i":{{i}}}""", default);
            }
        }

        var after = await store.ReadAfterAsync(jobs[1], 0, 1000, default);
        Assert.IsGreaterThanOrEqualTo(300, after.Count);
        Assert.IsTrue(after.Zip(after.Skip(1)).All(p => p.First.Seq < p.Second.Seq), "ascending");
        Assert.IsTrue(after.All(e => e.JobId == jobs[1].Value));

        var middle = after[200].Seq;
        var page = await store.ReadBeforeAsync(jobs[1], middle, 50, default);
        Assert.HasCount(50, page);
        Assert.AreEqual(after[150].Seq, page[0].Seq, "the 50 events just before, ascending");
        Assert.AreEqual(after[199].Seq, page[^1].Seq);

        var all = await store.ReadAfterAsync(null, 0, 10_000, default);
        Assert.IsTrue(all.All(e => e.IsSummary), "no agent.* in the all-jobs stream");
        Assert.IsTrue(all.Any(e => e.Type == "phase.set"));
    }

    [TestMethod]
    public async Task Events_land_in_monthly_partitions_and_partitions_are_created_ahead()
    {
        await using var db = await Database.CreateMigratedAsync(Name());
        var later = DateTimeOffset.UtcNow.AddMonths(3);

        await using (var ensure = db.CreateCommand("SELECT agentd.event_ensure_partitions($1)"))
        {
            ensure.Parameters.Add(new NpgsqlParameter { Value = later });
            Assert.AreEqual(2, (int)(await ensure.ExecuteScalarAsync())!, "that month and the next");
            Assert.AreEqual(0, (int)(await ensure.ExecuteScalarAsync())!, "idempotent");
        }

        await using (var insert = db.CreateCommand("INSERT INTO agentd.events (type, payload, ts) VALUES ('x', '{}', $1) RETURNING tableoid::regclass::text"))
        {
            insert.Parameters.Add(new NpgsqlParameter { Value = later });
            Assert.AreEqual($"agentd.events_{later:yyyy_MM}", (string)(await insert.ExecuteScalarAsync())!);
        }

        await using var span = db.CreateCommand("SELECT count(*) FROM agentd.events WHERE ts >= now() - interval '1 day'");
        Assert.IsGreaterThanOrEqualTo(1L, (long)(await span.ExecuteScalarAsync())!, "queries span partitions");
    }

    [TestMethod]
    public async Task Committed_events_stream_live_once_and_rolled_back_ones_never()
    {
        await using var db = await Database.CreateMigratedAsync(Name());
        var hub = new EventHub(NullLogger<EventHub>.Instance);
        var store = new EventStore(db);
        var listener = new EventNotificationListener(db, store, hub, NullLogger<EventNotificationListener>.Instance);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var received = new List<AgentEventDto>();
        var reading = Task.Run(async () =>
        {
            await foreach (var e in hub.Subscribe(null, stop.Token))
            {
                lock (received)
                {
                    received.Add(e);
                }
            }
        }, stop.Token);
        await listener.StartAsync(stop.Token);
        await Task.Delay(500, stop.Token);   // LISTEN is in place

        await using (var conn = await db.OpenConnectionAsync(stop.Token))
        await using (var tx = await conn.BeginTransactionAsync(stop.Token))
        {
            await using var rolledBack = new NpgsqlCommand("SELECT agentd.event_append(NULL, 'never.seen', '{}')", conn, tx);
            await rolledBack.ExecuteScalarAsync(stop.Token);
            await tx.RollbackAsync(stop.Token);
        }

        var seq = await store.AppendAsync(null, "job.committed", """{"ok":true}""", stop.Token);
        while (!stop.IsCancellationRequested && received.Count == 0)
        {
            await Task.Delay(50, stop.Token);
        }

        await listener.DeliverAsync(seq, stop.Token);   // a duplicate notification is ignored
        await Task.Delay(300, stop.Token);
        await stop.CancelAsync();
        await listener.StopAsync(default);

        Assert.AreEqual(seq, received.Single().Seq);
        Assert.AreEqual("job.committed", received[0].Type);
    }

    [TestMethod]
    public async Task Secrets_are_redacted_before_they_are_stored()
    {
        await using var db = await Database.CreateMigratedAsync(Name());
        var store = new EventStore(db);

        var seq = await store.AppendAsync(null, "agent.tool_result", """{"content":"Server=db;Password=hunter2pass;Database=x"}""", default);

        var stored = (await store.GetAsync(seq, default))!.Payload.GetProperty("content").GetString();
        Assert.AreEqual($"Server=db;Password={SecretRedactor.Mask};Database=x", stored);
    }

    private static string Name() => $"event_stream_{Interlocked.Increment(ref s_next)}";

    private static async Task<JobId> JobAsync(NpgsqlDataSource db)
    {
        var clock = new Clock();
        var job = Job.Create(WorkItemId.From(Interlocked.Increment(ref s_next) + 500), RepositoryName.From("sysmin"), "t", clock);
        await new JobRepository(db, clock).AddAsync(job, default);
        return job.Id;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
