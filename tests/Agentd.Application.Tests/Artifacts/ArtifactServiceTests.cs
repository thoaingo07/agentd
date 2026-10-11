using Agentd.Application.Artifacts;

namespace Agentd.Application.Tests.Artifacts;

[TestClass]
public sealed class ArtifactServiceTests
{
    private readonly Store _store = new();

    [TestMethod]
    public async Task A_fragment_becomes_a_page_named_after_its_title_with_a_random_hash()
    {
        var published = (await new ArtifactService(_store).PublishAsync("  Sync job: how a run pages through tables ", "<h1>Sync</h1>", "job", 12, "job #12", default)).Value!;

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

        await service.PublishAsync("Report", doc, "chat", 3, "chat #3", default);
        var noTitle = await service.PublishAsync(" ", "<p>x</p>", "chat", 3, "chat #3", default);
        var longTitle = await service.PublishAsync(new string('t', ArtifactService.MaxTitle + 1), "<p>x</p>", "chat", 3, "chat #3", default);
        var empty = await service.PublishAsync("Report", " ", "chat", 3, "chat #3", default);
        var huge = await service.PublishAsync("Report", new string('x', ArtifactService.MaxHtml + 1), "chat", 3, "chat #3", default);

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
    public async Task With_s3_configured_the_page_goes_to_storage_and_the_row_keeps_the_metadata()
    {
        var blobs = new Blobs();
        var service = new ArtifactService(_store, new Fakes.FakeClock(), S3Options(), blobs);

        var published = (await service.PublishAsync("Sync report", "<p>sync</p>", ArtifactSources.Idea, 4, "idea #4", default)).Value!;
        blobs.Failing = true;
        var failed = await service.PublishAsync("Another", "<p>x</p>", ArtifactSources.Idea, 4, "idea #4", default);
        var expired = await service.ExpireAsync(default);

        Assert.AreEqual((ArtifactStorage.S3, (string?)null), (_store.Storages[0], _store.Published[0].Html), "no HTML in the row");
        StringAssert.Contains(blobs.Objects[published.Name], "<body>\n<p>sync</p>\n</body>", "the whole document, under the row's name");
        Assert.AreEqual("validation", failed.Error!.Code);
        StringAssert.Contains(failed.Error.Message, "couldn't be uploaded");
        Assert.AreEqual(3, expired);
        CollectionAssert.AreEquivalent(new[] { "b-2", "c-3" }, blobs.Deleted, "only S3 pages have objects to delete");
    }

    [TestMethod]
    public async Task Reading_an_s3_page_fetches_it_and_says_when_its_gone_or_expired()
    {
        var blobs = new Blobs();
        blobs.Objects["sync-1"] = "<p>from r2</p>";
        var service = new ArtifactService(_store, null, S3Options(), blobs);

        _store.Page = new Artifact("sync-1", "Sync", null, "idea", 4, "idea #4", DateTimeOffset.UnixEpoch, null, ArtifactStorage.S3);
        var live = await service.ReadAsync("sync-1", default);
        _store.Page = _store.Page with { Name = "gone-2" };
        var missing = await service.ReadAsync("gone-2", default);
        _store.Page = _store.Page with { ExpiredAt = DateTimeOffset.UnixEpoch };
        var expired = await service.ReadAsync("gone-2", default);
        _store.Page = null;

        Assert.AreEqual("<p>from r2</p>", live!.Html);
        Assert.AreEqual((true, false), (missing!.Missing, missing.Expired));
        Assert.AreEqual((true, (string?)null), (expired!.Expired, expired.Html));
        Assert.IsNull(await service.ReadAsync("nope", default));
    }

    [TestMethod]
    [DataRow("https://agentd.thoaingo.com/", "s3", "https://agentd.thoaingo.com/artifacts/a-1.html")]
    [DataRow("https://agentd.thoaingo.com", "database", "https://web.example/artifacts/a-1.html")]
    [DataRow(null, "s3", "https://web.example/artifacts/a-1.html")]
    [DataRow("not a url", "s3", "https://web.example/artifacts/a-1.html")]
    [DataRow("ftp://agentd.thoaingo.com", "s3", "https://web.example/artifacts/a-1.html")]
    public void An_s3_pages_link_is_on_the_public_url_when_set_and_any_other_on_agentd(string? publicUrl, string storage, string link)
    {
        var options = S3Options();
        options.Value.S3.PublicUrl = publicUrl;

        Assert.AreEqual(link, new ArtifactService(_store, null, options).Link("a-1", storage, "https://web.example/"));
        Assert.AreEqual("/artifacts/a-1.html", new ArtifactService(_store).Link("a-1", ArtifactStorage.S3, null), "relative without an origin");
    }

    [TestMethod]
    public async Task Publishing_to_s3_says_where_the_page_went()
    {
        var published = (await new ArtifactService(_store, null, S3Options(), new Blobs()).PublishAsync("Sync", "<p>x</p>", ArtifactSources.Job, 1, "job #1", default)).Value!;
        var inDatabase = (await new ArtifactService(_store).PublishAsync("Other", "<p>x</p>", ArtifactSources.Job, 1, "job #1", default)).Value!;

        Assert.AreEqual((ArtifactStorage.S3, ArtifactStorage.Database), (published.Storage, inDatabase.Storage));
    }

    [TestMethod]
    public async Task Without_s3_keys_pages_stay_in_the_database()
    {
        var options = S3Options();
        options.Value.S3.SecretAccessKey = null;

        await new ArtifactService(_store, null, options, new Blobs()).PublishAsync("Sync", "<p>x</p>", ArtifactSources.Job, 1, "job #1", default);

        Assert.AreEqual(ArtifactStorage.Database, _store.Storages.Single());
        StringAssert.Contains(_store.Published.Single().Html, "<p>x</p>");
    }

    private static Microsoft.Extensions.Options.IOptions<ArtifactOptions> S3Options() => Microsoft.Extensions.Options.Options.Create(new ArtifactOptions
    {
        S3 = new ArtifactS3Options { Url = "https://acct.r2.cloudflarestorage.com/agentd", AccessKeyId = "id", SecretAccessKey = "secret" },
    });

    private sealed class Blobs : IArtifactBlobs
    {
        public Dictionary<string, string> Objects { get; } = [];

        public List<string> Deleted { get; } = [];

        public bool Failing { get; set; }

        public Task PutAsync(string name, string html, CancellationToken cancellationToken)
        {
            if (Failing)
            {
                throw new InvalidOperationException("R2 answered 403");
            }

            Objects[name] = html;
            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Objects.GetValueOrDefault(name));

        public Task DeleteAsync(string name, CancellationToken cancellationToken)
        {
            Deleted.Add(name);
            return Task.CompletedTask;
        }
    }

    [TestMethod]
    [DataRow("Café — Crème & Co", "caf-crme-co")]
    [DataRow("!!!", "artifact")]
    [DataRow("A very long title that keeps going well past the forty-eight character limit", "a-very-long-title-that-keeps-going-well-past-the")]
    public void Slugs_are_ascii_words_joined_by_dashes(string title, string slug) => Assert.AreEqual(slug, ArtifactService.Slug(title));

    private sealed class Store : IArtifactStore
    {
        public List<(string Name, string Title, string Html, long? Job)> Published { get; } = [];

        public List<string> Storages { get; } = [];

        public Artifact? Page { get; set; }

        public Task<PublishedArtifact> PublishAsync(string name, string title, string? html, string source, long sourceId, string author, string storage, CancellationToken cancellationToken)
        {
            Published.Add((name, title, html!, source == ArtifactSources.Job ? sourceId : null));
            Storages.Add(storage);
            return Task.FromResult(new PublishedArtifact(name, true));
        }

        public Task<Artifact?> GetAsync(string name, CancellationToken cancellationToken) => Task.FromResult(Page);

        public Task<IReadOnlyList<ArtifactSummary>> ListAsync(int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public List<DateTimeOffset> Expired { get; } = [];

        public Task<IReadOnlyList<ExpiredArtifact>> ExpireAsync(DateTimeOffset updatedBefore, CancellationToken cancellationToken)
        {
            Expired.Add(updatedBefore);
            return Task.FromResult<IReadOnlyList<ExpiredArtifact>>([new("a-1", ArtifactStorage.Database), new("b-2", ArtifactStorage.S3), new("c-3", ArtifactStorage.S3)]);
        }
    }
}
