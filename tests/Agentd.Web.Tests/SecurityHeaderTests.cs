using System.Net;

namespace Agentd.Web.Tests;

/// <summary>The daemon's real pipeline: every kind of response carries the CSP and the security headers.</summary>
[TestClass]
public sealed class SecurityHeaderTests
{
    private static AgentdHostFactory s_factory = null!;

    [ClassInitialize]
    public static void Init(TestContext _) => s_factory = new AgentdHostFactory();

    [ClassCleanup]
    public static void Cleanup() => s_factory.Dispose();

    [TestMethod]
    [DataRow("/", "no-cache")]
    [DataRow("/jobs/42", "no-cache")]
    [DataRow("/_content/Agentd.Web/assets/dashboard-abc123.js", "public, max-age=31536000, immutable")]
    [DataRow("/_content/Agentd.Web/theme-init.js", null)]
    [DataRow("/bff/user", "no-store")]
    [DataRow("/api/nope", "no-store")]
    public async Task Every_response_carries_the_csp_and_security_headers(string path, string? cacheControl)
    {
        using var client = s_factory.CreateClient();
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        var csp = Header(response, "Content-Security-Policy");
        foreach (var directive in new[] { "default-src 'none'", "script-src 'self'", "style-src 'self'", "frame-ancestors 'none'", "object-src 'none'", "base-uri 'none'", "report-to csp" })
        {
            StringAssert.Contains(csp, directive);
        }

        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("upgrade-insecure-requests", csp, "plain http on loopback");
        StringAssert.Contains(Header(response, "Content-Security-Policy-Report-Only"), "require-trusted-types-for 'script'");
        Assert.AreEqual("csp=\"/api/csp-report\"", Header(response, "Reporting-Endpoints"));
        Assert.AreEqual("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.AreEqual("no-referrer", Header(response, "Referrer-Policy"));
        Assert.AreEqual("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        StringAssert.Contains(Header(response, "Permissions-Policy"), "camera=()");
        Assert.IsFalse(response.Headers.Contains("Strict-Transport-Security"), "HSTS only over https");
        if (cacheControl is not null)
        {
            Assert.AreEqual(cacheControl, response.Headers.CacheControl?.ToString());
        }
    }

    [TestMethod]
    public async Task Https_adds_hsts_and_upgrades_insecure_requests()
    {
        using var client = s_factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        StringAssert.StartsWith(Header(response, "Strict-Transport-Security"), "max-age=");
        StringAssert.Contains(Header(response, "Content-Security-Policy"), "upgrade-insecure-requests");
    }

    [TestMethod]
    public async Task Csp_reports_are_accepted_without_a_token()
    {
        using var client = s_factory.CreateClient();
        using var body = new StringContent("""[{"type":"csp-violation","body":{"effectiveDirective":"script-src-elem","blockedURL":"inline","documentURL":"http://127.0.0.1:7780/","disposition":"enforce"}}]""");
        body.Headers.ContentType = new("application/reports+json");

        using var response = await client.PostAsync(new Uri("/api/csp-report", UriKind.Relative), body);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static string Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(",", values)
            : throw new AssertFailedException($"{name} is missing on {response.RequestMessage?.RequestUri}");
}
