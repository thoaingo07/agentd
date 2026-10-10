using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Persistence.Repositories;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class ArtifactStoreTests
{
    [TestMethod]
    public async Task Publishing_the_same_title_again_replaces_the_page_and_keeps_its_link_per_source()
    {
        await using var db = await Database.CreateMigratedAsync("artifacts_publish");
        var store = new ArtifactStore(db);
        var job = await JobAsync(db);
        var events = new EventStore(db);
        var from = await events.LatestSeqAsync(default);

        var first = await store.PublishAsync("sync-report-aaaa1111", "Sync report", "<p>v1</p>", job.Value, null, "job #1", default);
        var again = await store.PublishAsync("sync-report-bbbb2222", "Sync report", "<p>v2</p>", job.Value, null, "job #1", default);
        var chat = await store.PublishAsync("sync-report-cccc3333", "Sync report", "<p>chat</p>", null, 7, "chat #7", default);
        var loose = await store.PublishAsync("sync-report-dddd4444", "Sync report", "<p>none</p>", null, null, "web", default);

        Assert.AreEqual(("sync-report-aaaa1111", true), (first.Name, first.Created));
        Assert.AreEqual(("sync-report-aaaa1111", false), (again.Name, again.Created), "same source and title: replaced in place");
        Assert.AreEqual((true, true), (chat.Created, loose.Created), "another source is another page");
        var page = (await store.GetAsync("sync-report-aaaa1111", default))!;
        Assert.AreEqual(("Sync report", "<p>v2</p>", (long?)job.Value, (long?)null), (page.Title, page.Html, page.JobId, page.ChatId));
        Assert.IsNull(await store.GetAsync("sync-report-bbbb2222", default));
        var announced = (await events.ReadAfterAsync(job, from, 10, default)).Where(e => e.Type == "artifact.published").ToList();
        Assert.HasCount(2, announced, "a job's page is announced on its stream, each time");
        Assert.IsFalse(announced[1].Payload.GetProperty("created").GetBoolean());
    }

    [TestMethod]
    public async Task Parallel_publishes_of_one_title_make_one_page_and_the_list_is_newest_first_without_html()
    {
        await using var db = await Database.CreateMigratedAsync("artifacts_parallel");
        var store = new ArtifactStore(db);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            store.PublishAsync($"diagram-{i:x8}", "Diagram", $"<p>{i}</p>", null, 3, "chat #3", default))));
        await store.PublishAsync("later-00000001", "Later", "<p>later</p>", null, 3, "chat #3", default);

        Assert.AreEqual(1, results.Count(r => r.Created), "one of them created it");
        Assert.AreEqual(1, results.Select(r => r.Name).Distinct().Count(), "everyone got its link");
        var list = await store.ListAsync(10, default);
        CollectionAssert.AreEqual(new[] { "Later", "Diagram" }, list.Select(a => a.Title).ToArray());
        Assert.AreEqual(((long?)3, "chat #3"), (list[0].ChatId, list[0].Author));
        Assert.HasCount(1, await store.ListAsync(1, default));
    }

    private static async Task<JobId> JobAsync(Npgsql.NpgsqlDataSource db)
    {
        var clock = new Clock();
        var job = Job.Create(WorkItemId.From(5620), RepositoryName.From("sysmin"), "t", clock);
        await new JobRepository(db, clock).AddAsync(job, default);
        return job.Id;
    }

    private sealed class Clock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
