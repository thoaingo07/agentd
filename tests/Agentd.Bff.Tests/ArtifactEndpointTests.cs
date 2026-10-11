using System.Net;
using System.Text.Json;
using Agentd.Application.Artifacts;
using Agentd.Bff.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class ArtifactEndpointTests
{
    private static readonly Artifact s_page = new("sync-report-1a2b3c4d", "Sync report", "<!doctype html><html><body><script>alert(1)</script></body></html>", 12, null, "job #12",
        DateTimeOffset.UnixEpoch);

    [TestMethod]
    public async Task An_artifact_is_served_as_html_in_a_sandbox_that_cant_reach_agentd()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var page = await client.GetAsync(new Uri("/artifacts/sync-report-1a2b3c4d.html", UriKind.Relative));
        using var missing = await client.GetAsync(new Uri("/artifacts/nope-00000000.html", UriKind.Relative));
        using var odd = await client.GetAsync(new Uri("/artifacts/Sync-Report.html", UriKind.Relative));
        using var noExtension = await client.GetAsync(new Uri("/artifacts/sync-report-1a2b3c4d", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.AreEqual("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.AreEqual(s_page.Html, await page.Content.ReadAsStringAsync());
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        StringAssert.StartsWith(csp, "sandbox allow-scripts allow-popups");
        Assert.DoesNotContain("allow-same-origin", csp, "an opaque origin: no cookies, storage or API of agentd's");
        StringAssert.Contains(csp, "connect-src 'none'");
        Assert.IsFalse(page.Headers.Contains("Content-Security-Policy-Report-Only"));
        Assert.AreEqual((HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound), (missing.StatusCode, odd.StatusCode, noExtension.StatusCode));
    }

    private static readonly Artifact s_expired = new("old-notes-99887766", "Old <notes>", null, 3, null, "job #3", DateTimeOffset.UnixEpoch, new DateTimeOffset(2026, 11, 9, 0, 0, 0, TimeSpan.Zero));

    [TestMethod]
    public async Task An_expired_artifacts_link_says_so()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var gone = await client.GetAsync(new Uri("/artifacts/old-notes-99887766.html", UriKind.Relative));
        var body = await gone.Content.ReadAsStringAsync();

        Assert.AreEqual(HttpStatusCode.Gone, gone.StatusCode);
        StringAssert.Contains(body, "<h1>Old &lt;notes&gt;</h1>");
        StringAssert.Contains(body, "This artifact expired on 2026-11-09.");
        StringAssert.StartsWith(gone.Headers.GetValues("Content-Security-Policy").Single(), "sandbox ");
    }

    [TestMethod]
    public async Task Artifacts_are_listed_with_their_links()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        var list = JsonDocument.Parse(await client.GetStringAsync(new Uri("/api/artifacts", UriKind.Relative))).RootElement;

        CollectionAssert.AreEqual(new[] { "name", "title", "url", "jobId", "chatId", "author", "createdAt", "updatedAt" }, list[0].EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        Assert.AreEqual("/artifacts/sync-report-1a2b3c4d.html", list[0].GetProperty("url").GetString());
    }

    [TestMethod]
    [DataRow("/artifacts/x.html", true)]
    [DataRow("/artifacts", false)]
    [DataRow("/artifacts/", false)]
    [DataRow("/artifactsx/y.html", false)]
    [DataRow("/api/artifacts", false)]
    public void Only_an_artifacts_page_gets_the_sandbox_not_the_apps_list(string path, bool sandboxed) =>
        Assert.AreEqual(sandboxed, SecurityHeaders.IsArtifact(new PathString(path)));

    private static async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton<IArtifactStore>(new Store());
        var app = builder.Build();
        app.UseSecurityHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Store : IArtifactStore
    {
        public Task<PublishedArtifact> PublishAsync(string name, string title, string html, long? jobId, long? chatId, string author, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<Artifact?> GetAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult(name == s_page.Name ? s_page : name == s_expired.Name ? s_expired : null);

        public Task<int> ExpireAsync(DateTimeOffset updatedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ArtifactSummary>> ListAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ArtifactSummary>>([new(s_page.Name, s_page.Title, s_page.JobId, null, s_page.Author, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)]);
    }
}
