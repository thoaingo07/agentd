using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class WorkItemHistoryTests
{
    private static int s_next;

    [TestMethod]
    public async Task Events_of_every_job_come_back_in_order_and_page_both_ways()
    {
        await using var db = await Database.CreateMigratedAsync($"work_item_history_{Interlocked.Increment(ref s_next)}");
        var clock = new Clock();
        var jobs = new JobRepository(db, clock);
        var events = new EventStore(db);
        var history = new WorkItemHistoryStore(db, clock);
        var wi = WorkItemId.From(5613);

        var ids = new List<JobId>();
        for (var run = 0; run < 3; run++)
        {
            var job = Job.Create(wi, RepositoryName.From("sysmin"), "Refine the AGENTS.md", clock);
            await jobs.AddAsync(job, default);
            for (var i = 0; i < 4; i++)
            {
                await events.AppendAsync(job.Id, "phase.set", $$"""{"run":{{run}},"i":{{i}}}""", default);
            }

            if (run < 2)
            {
                job.Cancel("tngo");   // one active job per work item: finish it before the rerun
                await jobs.SaveAsync(job, default);
            }

            ids.Add(job.Id);
        }

        var other = Job.Create(WorkItemId.From(9999), RepositoryName.From("sysmin"), "Other", clock);
        await jobs.AddAsync(other, default);
        await events.AppendAsync(other.Id, "phase.set", "{}", default);

        CollectionAssert.AreEqual(ids.Select(i => i.Value).ToArray(), (await history.ListJobsAsync(wi, default)).Select(j => j.Id.Value).ToArray(), "oldest first");

        var all = await history.ReadEventsAsync(wi, null, null, 1000, default);
        Assert.IsTrue(all.All(e => ids.Any(i => i.Value == e.JobId)), "only this work item's jobs");
        Assert.IsTrue(all.Zip(all.Skip(1)).All(p => p.First.Seq < p.Second.Seq), "ascending");
        CollectionAssert.IsSubsetOf(ids.Select(i => (long?)i.Value).ToArray(), all.Select(e => e.JobId).Distinct().ToArray());

        var forward = await history.ReadEventsAsync(wi, all[2].Seq, null, 3, default);
        CollectionAssert.AreEqual(all.Skip(3).Take(3).Select(e => e.Seq).ToArray(), forward.Select(e => e.Seq).ToArray(), "after: the next ones, ascending");
        var backward = await history.ReadEventsAsync(wi, null, all[^1].Seq, 3, default);
        CollectionAssert.AreEqual(all.SkipLast(1).TakeLast(3).Select(e => e.Seq).ToArray(), backward.Select(e => e.Seq).ToArray(), "before: the newest earlier ones, ascending");
    }

    [TestMethod]
    public async Task Posted_messages_keep_their_delivery_status_and_skip_heartbeats()
    {
        await using var db = await Database.CreateMigratedAsync($"work_item_history_{Interlocked.Increment(ref s_next)}");
        var clock = new Clock();
        var job = Job.Create(WorkItemId.From(5613), RepositoryName.From("sysmin"), "t", clock);
        await new JobRepository(db, clock).AddAsync(job, default);
        foreach (var (kind, status, markdown, heartbeat) in new[] { ("Info", "sent", "📤 Pushed", false), ("Status", "sent", "💓 still working", true), ("Question", "dead", "Which endpoint?", false) })
        {
            await using var insert = db.CreateCommand("INSERT INTO agentd.outbound_messages (job_id, provider, kind, payload, status) VALUES ($1, 'discord', $2, $3::jsonb, $4)");
            insert.Parameters.Add(new NpgsqlParameter { Value = job.Id.Value });
            insert.Parameters.Add(new NpgsqlParameter { Value = kind });
            insert.Parameters.Add(new NpgsqlParameter { Value = $$"""{"markdown":"{{markdown}}","replaceStatusMessage":{{(heartbeat ? "true" : "false")}}}""" });
            insert.Parameters.Add(new NpgsqlParameter { Value = status });
            await insert.ExecuteNonQueryAsync();
        }

        var posted = await new WorkItemHistoryStore(db, clock).ListPostedAsync(WorkItemId.From(5613), 100, default);

        CollectionAssert.AreEqual(new[] { "📤 Pushed|sent", "Which endpoint?|dead" }, posted.Select(p => $"{p.Markdown}|{p.Status}").ToArray());
        Assert.AreEqual("discord", posted[0].Provider);
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
