using System.Net;
using System.Text.RegularExpressions;

namespace Agentd.Web.Tests;

[TestClass]
public sealed partial class SpaHostingTests
{
    private static AgentdHostFactory s_factory = null!;
    private static HttpClient s_client = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        s_factory = new AgentdHostFactory();
        s_client = s_factory.CreateClient();
    }

    [ClassCleanup]
    public static void Cleanup()
    {
        s_client.Dispose();
        s_factory.Dispose();
    }

    [TestMethod]
    [DataRow("/")]
    [DataRow("/jobs/42")]
    [DataRow("/history")]
    public async Task Client_routes_render_the_razor_shell(string path)
    {
        using var response = await s_client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "<div id=\"app\"></div>");
    }

    [TestMethod]
    [DataRow("/setup/wizard")]
    [DataRow("/setup/wizard/azure-devops")]
    public async Task The_setup_wizard_has_its_own_shell(string path)
    {
        var html = await s_client.GetStringAsync(new Uri(path, UriKind.Relative));

        StringAssert.Contains(html, """<script type="module" src="/_content/Agentd.Web/assets/setup-0f1e2d.js"></script>""");
        Assert.DoesNotContain("dashboard-abc123.js", html);
        Assert.IsFalse(InlineScript().IsMatch(html), "inline <script> content found (CSP forbids it)");
    }

    [TestMethod]
    public async Task Shell_references_assets_from_the_vite_manifest()
    {
        var html = await s_client.GetStringAsync(new Uri("/", UriKind.Relative));

        StringAssert.Contains(html, """<script type="module" src="/_content/Agentd.Web/assets/dashboard-abc123.js"></script>""");
        StringAssert.Contains(html, """<link rel="stylesheet" href="/_content/Agentd.Web/assets/dashboard-abc123.css" />""");
        StringAssert.Contains(html, """<link rel="stylesheet" href="/_content/Agentd.Web/assets/vendor-def456.css" />""");
        StringAssert.Contains(html, """<link rel="modulepreload" href="/_content/Agentd.Web/assets/vendor-def456.js" />""");
        StringAssert.Contains(html, """<script src="/_content/Agentd.Web/theme-init.js"></script>""");
        Assert.DoesNotContain("/@vite/client", html);
    }

    [TestMethod]
    public async Task Shell_has_no_inline_script_or_style()
    {
        var html = await s_client.GetStringAsync(new Uri("/", UriKind.Relative));

        Assert.IsFalse(InlineScript().IsMatch(html), "inline <script> content found (CSP forbids it)");
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotContain(" style=", html);
    }

    [TestMethod]
    [DataRow("/api")]
    [DataRow("/api/nope")]
    [DataRow("/bff/whatever")]
    [DataRow("/hubs/events")]
    [DataRow("/mcp/nope")]
    public async Task Reserved_paths_never_fall_back_to_the_spa(string path)
    {
        using var response = await s_client.GetAsync(new Uri(path, UriKind.Relative));

        // /hubs/* is refused by the hub origin guard (403) before routing; the rest is a plain 404.
        var expected = path.StartsWith("/hubs", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound;
        Assert.AreEqual(expected, response.StatusCode);
    }

    [TestMethod]
    public async Task Mcp_endpoint_requires_a_job_token()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp", UriKind.Relative));
        request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        using var response = await s_client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Mcp_endpoint_refuses_tunnelled_requests()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp", UriKind.Relative));
        request.Headers.Add("Cf-Connecting-IP", "203.0.113.9");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "anything");

        using var response = await s_client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Mcp_endpoint_refuses_browser_requests()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/mcp", UriKind.Relative));
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "anything");

        using var response = await s_client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Built_static_files_are_served()
    {
        using var response = await s_client.GetAsync(new Uri("/_content/Agentd.Web/theme-init.js", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // <script ...> followed by anything other than </script> means inline code.
    [GeneratedRegex(@"<script[^>]*>\s*[^<\s]", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();
}
