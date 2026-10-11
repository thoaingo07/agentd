using Agentd.Application.Artifacts;

namespace Agentd.Application.Tests.Artifacts;

[TestClass]
public sealed class ArtifactServiceTests
{
    private readonly Store _store = new();

    [TestMethod]
    public async Task A_fragment_becomes_a_page_named_after_its_title_with_a_random_hash()
    {
        var published = (await new ArtifactService(_store).PublishAsync("  Sync job: how a run pages through tables ", "<h1>Sync</h1>", 12, null, "job #12", default)).Value!;

        var (name, title, html, job) = _store.Published.Single();
        StringAssert.Matches(name, new System.Text.RegularExpressions.Regex("^sync-job-how-a-run-pages-through-tables-[0-9a-f]{8}$"));
        Assert.AreEqual(($"/artifacts/{name}.html", "Sync job: how a run pages through tables", (long?)12), (published.Path, title, job));
        StringAssert.StartsWith(html, "<!doctype html>");
        StringAssert.Contains(html, "<title>Sync job: how a run pages through tables</title>");
        StringAssert.Contains(html, "<body>\n<h1>Sync</h1>\n</body>");
    }

    [TestMethod]
    public async Task A_whole_document_is_kept_and_titles_and_html_are_checked()
    {
        var service = new ArtifactService(_store);
        const string doc = "<!DOCTYPE html><HTML><body>x</body></HTML>";

        await service.PublishAsync("Report", doc, null, 3, "chat #3", default);
        var noTitle = await service.PublishAsync(" ", "<p>x</p>", null, 3, "chat #3", default);
        var longTitle = await service.PublishAsync(new string('t', ArtifactService.MaxTitle + 1), "<p>x</p>", null, 3, "chat #3", default);
        var empty = await service.PublishAsync("Report", " ", null, 3, "chat #3", default);
        var huge = await service.PublishAsync("Report", new string('x', ArtifactService.MaxHtml + 1), null, 3, "chat #3", default);

        Assert.AreEqual(doc, _store.Published.Single().Html);
        Assert.IsTrue(new[] { noTitle, longTitle, empty, huge }.All(r => r.Error!.Code == "validation"));
    }

    [TestMethod]
    public async Task Pages_expire_after_the_retention_and_zero_keeps_them_forever()
    {
        var clock = new Fakes.FakeClock();
        var thirty = new ArtifactService(_store, clock, Microsoft.Extensions.Options.Options.Create(new ArtifactOptions()));
        var forever = new ArtifactService(_store, clock, Microsoft.Extensions.Options.Options.Create(new ArtifactOptions { RetentionDays = 0 }));

        Assert.AreEqual(3, await thirty.ExpireAsync(default));
        Assert.AreEqual(0, await forever.ExpireAsync(default));

        Assert.AreEqual(clock.UtcNow.AddDays(-30), _store.Expired.Single(), "30 days after the last update, by default");
    }

    [TestMethod]
    [DataRow("Café — Crème & Co", "caf-crme-co")]
    [DataRow("!!!", "artifact")]
    [DataRow("A very long title that keeps going well past the forty-eight character limit", "a-very-long-title-that-keeps-going-well-past-the")]
    public void Slugs_are_ascii_words_joined_by_dashes(string title, string slug) => Assert.AreEqual(slug, ArtifactService.Slug(title));

    private sealed class Store : IArtifactStore
    {
        public List<(string Name, string Title, string Html, long? Job)> Published { get; } = [];

        public Task<PublishedArtifact> PublishAsync(string name, string title, string html, long? jobId, long? chatId, string author, CancellationToken cancellationToken)
        {
            Published.Add((name, title, html, jobId));
            return Task.FromResult(new PublishedArtifact(name, true));
        }

        public Task<Artifact?> GetAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ArtifactSummary>> ListAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public List<DateTimeOffset> Expired { get; } = [];

        public Task<int> ExpireAsync(DateTimeOffset updatedBefore, CancellationToken cancellationToken)
        {
            Expired.Add(updatedBefore);
            return Task.FromResult(3);
        }
    }
}
