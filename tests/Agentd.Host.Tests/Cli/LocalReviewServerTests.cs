using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentd.Application.Reviews;
using Agentd.Host.Cli.Review;

namespace Agentd.Host.Tests.Cli;

[TestClass]
public sealed class LocalReviewServerTests
{
    private static readonly ReviewResult s_result = new("One real bug.", [
        new ReviewFinding("breaks", "src/Sync.cs", 2, "Unbounded read", "Large tables load at once.", "Page by the primary key."),
        new ReviewFinding("performance", "src/Lookup.cs", 9, "N+1 lookups", "One query per row.", "Batch them."),
    ]);

    private string? _written;

    [TestMethod]
    public async Task Only_the_printed_link_gets_in_and_send_writes_the_kept_and_edited_findings_and_comments()
    {
        await using var server = await StartAsync();
        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri(server.Url.GetLeftPart(UriPartial.Authority)) };

        using var stranger = await http.GetAsync(new Uri("/api/reviews/1", UriKind.Relative));
        using var link = await http.GetAsync(server.Url);
        Assert.AreEqual(HttpStatusCode.Forbidden, stranger.StatusCode, "no cookie, no review");
        Assert.AreEqual((HttpStatusCode.Redirect, "/reviews/1"), (link.StatusCode, link.Headers.Location!.ToString()));
        StringAssert.Contains(link.Headers.GetValues("Content-Security-Policy").Single(), "script-src 'self'");

        var detail = JsonDocument.Parse(await http.GetStringAsync(new Uri("/api/reviews/1", UriKind.Relative))).RootElement;
        Assert.AreEqual(("Ready", 2, 1), (detail.GetProperty("session").GetProperty("status").GetString(), detail.GetProperty("session").GetProperty("findings").GetArrayLength(),
            detail.GetProperty("session").GetProperty("findings")[0].GetProperty("number").GetInt32()));
        var diff = JsonDocument.Parse(await http.GetStringAsync(new Uri("/api/reviews/1/diff", UriKind.Relative))).RootElement;
        Assert.AreEqual("src/Sync.cs", diff.GetProperty("files")[0].GetString());

        using var forged = await http.PutAsJsonAsync(new Uri("/api/reviews/1/findings/2", UriKind.Relative), new { decision = "dropped" });
        Assert.AreEqual(HttpStatusCode.BadRequest, forged.StatusCode, "changes need the antiforgery token");
        var token = JsonDocument.Parse(await http.GetStringAsync(new Uri("/bff/antiforgery", UriKind.Relative))).RootElement.GetProperty("token").GetString()!;
        http.DefaultRequestHeaders.Add("X-XSRF-TOKEN", token);

        using var edit = await http.PutAsJsonAsync(new Uri("/api/reviews/1/findings/1", UriKind.Relative), new { decision = "edited", text = "Page it by key, 5000 rows at a time." });
        using var drop = await http.PutAsJsonAsync(new Uri("/api/reviews/1/findings/2", UriKind.Relative), new { decision = "dropped" });
        using var comment = await http.PostAsJsonAsync(new Uri("/api/reviews/1/comments", UriKind.Relative), new { file = "src/Sync.cs", line = 5, text = "make the size config" });
        using var send = await http.PostAsync(new Uri("/api/reviews/1/send", UriKind.Relative), null);

        Assert.AreEqual((HttpStatusCode.NoContent, HttpStatusCode.NoContent, HttpStatusCode.Created, HttpStatusCode.OK), (edit.StatusCode, drop.StatusCode, comment.StatusCode, send.StatusCode));
        Assert.AreEqual(".agentd/review.md", await server.Sent.WaitAsync(TimeSpan.FromSeconds(5)));
        StringAssert.Contains(_written, "1. 🔴 **Unbounded read** · `src/Sync.cs:2`\n   Page it by key, 5000 rows at a time.");
        StringAssert.Contains(_written, "2. 💬 On `src/Sync.cs:5`: make the size config");
        Assert.DoesNotContain("N+1 lookups", _written!, "dropped findings stay out");
        using var late = await http.PostAsJsonAsync(new Uri("/api/reviews/1/comments", UriKind.Relative), new { text = "too late" });
        Assert.AreEqual(HttpStatusCode.Conflict, late.StatusCode);
    }

    [TestMethod]
    public async Task The_page_is_the_web_uis_review_app_with_its_own_assets()
    {
        if (!LocalReviewServer.Available)
        {
            Assert.Inconclusive("Built without the web UI (the .NET-only CI job).");
        }

        await using var server = await StartAsync();
        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new Uri(server.Url.GetLeftPart(UriPartial.Authority)) };

        var html = await http.GetStringAsync(server.Url);   // follows the redirect with the cookie
        var script = System.Text.RegularExpressions.Regex.Match(html, "src=\"(/_content/Agentd.Web/assets/review-[^\"]+\\.js)\"").Groups[1].Value;
        using var asset = await http.GetAsync(new Uri(script, UriKind.Relative));

        StringAssert.Contains(html, "<div id=\"app\"></div>");
        Assert.AreEqual(HttpStatusCode.OK, asset.StatusCode, script);
    }

    private Task<LocalReviewServer> StartAsync()
    {
        var session = new LocalReviewSession("sysmin", "HEAD", "HEAD", "diff --git a/src/Sync.cs b/src/Sync.cs\n", ["src/Sync.cs"], s_result, "dev");
        return LocalReviewServer.StartAsync(session, text =>
        {
            _written = text;
            return Task.FromResult(".agentd/review.md");
        }, CancellationToken.None);
    }
}
