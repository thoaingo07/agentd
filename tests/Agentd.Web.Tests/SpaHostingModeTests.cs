using System.Net;

namespace Agentd.Web.Tests;

[TestClass]
public sealed class SpaHostingModeTests
{
    [TestMethod]
    public async Task Missing_manifest_in_production_returns_503_with_guidance()
    {
        using var factory = new AgentdHostFactory(withManifest: false);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "npm run build");
    }

    [TestMethod]
    public async Task Development_with_dev_server_loads_modules_from_vite()
    {
        using var factory = new AgentdHostFactory(
            new Dictionary<string, string?> { ["Agentd:Web:Vite:DevServerUrl"] = "http://127.0.0.1:5999" },
            environment: "Development",
            withManifest: false);
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        StringAssert.Contains(html, """<script type="module" src="/_content/Agentd.Web/@vite/client"></script>""");
        StringAssert.Contains(html, """<script type="module" src="/_content/Agentd.Web/ClientApps/dashboard/main.ts"></script>""");
    }

    [TestMethod]
    public async Task Aspire_web_reference_configures_the_dev_server()
    {
        using var factory = new AgentdHostFactory(
            new Dictionary<string, string?> { ["services:web:http:0"] = "http://127.0.0.1:5998" },
            environment: "Development",
            withManifest: false);
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync(new Uri("/", UriKind.Relative));

        StringAssert.Contains(html, "/_content/Agentd.Web/@vite/client");
    }
}
