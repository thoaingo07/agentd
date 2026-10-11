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

        var first = await store.PublishAsync("sync-report-aaaa1111", "Sync report", "<p>v1</p>", "job", job.Value, "job #1", default);
        var again = await store.PublishAsync("sync-report-bbbb2222", "Sync report", "<p>v2</p>", "job", job.Value, "job #1", default);
        var chat = await store.PublishAsync("sync-report-cccc3333", "Sync report", "<p>chat</p>", "chat", 7, "chat #7", default);
        var loose = await store.PublishAsync("sync-report-dddd4444", "Sync report", "<p>none</p>", "web", 0, "web", default);

        Assert.AreEqual(("sync-report-aaaa1111", true), (first.Name, first.Created));
        Assert.AreEqual(("sync-report-aaaa1111", false), (again.Name, again.Created), "same source and title: replaced in place");
        Assert.AreEqual((true, true), (chat.Created, loose.Created), "another source is another page");
        var page = (await store.GetAsync("sync-report-aaaa1111", default))!;
        Assert.AreEqual(("Sync report", "<p>v2</p>", "job", job.Value), (page.Title, page.Html, page.Source, page.SourceId));
        Assert.IsNull(await store.GetAsync("sync-report-bbbb2222", default));
        var announced = (await events.ReadAfterAsync(job, from, 10, default)).Where(e => e.Type == "artifact.published").ToList();
        Assert.HasCount(2, announced, "a job's page is announced on its stream, each time");
        Assert.IsFalse(announced[1].Payload.GetProperty("created").GetBoolean());
    }

    [TestMethod]
    public async Task Every_kind_of_agent_publishes_its_own_page_even_with_the_same_id_and_title()
    {
        await using var db = await Database.CreateMigratedAsync("artifacts_sources");
        var store = new ArtifactStore(db);

        var sources = new[] { "idea", "review", "review-session", "watch", "chat" };
        var published = await Task.WhenAll(sources.Select((s, i) => Task.Run(() => store.PublishAsync($"notes-{i:x8}", "Notes", $"<p>{s}</p>", s, 5, $"{s} #5", default))));

        Assert.IsTrue(published.All(p => p.Created), "the source is part of what a page is");
        var list = await store.ListAsync(10, default);
        CollectionAssert.AreEquivalent(sources, list.Select(a => a.Source).ToArray());
        Assert.IsTrue(list.All(a => a.SourceId == 5));
        Assert.AreEqual("<p>review-session</p>", (await store.GetAsync(published[2].Name, default))!.Html);
    }

    [TestMethod]
    public async Task Parallel_publishes_of_one_title_make_one_page_and_the_list_is_newest_first_without_html()
    {
        await using var db = await Database.CreateMigratedAsync("artifacts_parallel");
        var store = new ArtifactStore(db);

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            store.PublishAsync($"diagram-{i:x8}", "Diagram", $"<p>{i}</p>", "chat", 3, "chat #3", default))));
        await store.PublishAsync("later-00000001", "Later", "<p>later</p>", "chat", 3, "chat #3", default);

        Assert.AreEqual(1, results.Count(r => r.Created), "one of them created it");
        Assert.AreEqual(1, results.Select(r => r.Name).Distinct().Count(), "everyone got its link");
        var list = await store.ListAsync(10, default);
        CollectionAssert.AreEqual(new[] { "Later", "Diagram" }, list.Select(a => a.Title).ToArray());
        Assert.AreEqual(("chat", 3L, "chat #3"), (list[0].Source, list[0].SourceId, list[0].Author));
        Assert.HasCount(1, await store.ListAsync(1, default));
    }

    [TestMethod]
    public async Task Old_pages_expire_to_a_tombstone_once_even_with_parallel_sweeps_and_republishing_brings_them_back()
    {
        await using var db = await Database.CreateMigratedAsync("artifacts_expire");
        var store = new ArtifactStore(db);
        await store.PublishAsync("old-11111111", "Old", "<p>old</p>", "chat", 5, "chat #5", default);
        await store.PublishAsync("older-22222222", "Older", "<p>older</p>", "chat", 5, "chat #5", default);
        var cutoff = DateTimeOffset.UtcNow.AddSeconds(1);
        await Task.Delay(1100);
        await store.PublishAsync("fresh-33333333", "Fresh", "<p>fresh</p>", "chat", 5, "chat #5", default);

        var counts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => store.ExpireAsync(cutoff, default))));

        Assert.AreEqual(2, counts.Sum(), "each page expires once");
        var old = (await store.GetAsync("old-11111111", default))!;
        Assert.AreEqual(((string?)null, true), (old.Html, old.ExpiredAt is not null), "a tombstone: no HTML, the date it expired");
        CollectionAssert.AreEqual(new[] { "Fresh" }, (await store.ListAsync(10, default)).Select(a => a.Title).ToArray(), "expired pages aren't listed");

        var back = await store.PublishAsync("old-44444444", "Old", "<p>again</p>", "chat", 5, "chat #5", default);
        var revived = (await store.GetAsync("old-11111111", default))!;
        Assert.AreEqual(("old-11111111", "<p>again</p>", (DateTimeOffset?)null), (back.Name, revived.Html, revived.ExpiredAt), "same link, alive again");
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
