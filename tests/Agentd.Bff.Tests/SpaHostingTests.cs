using System.Net;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class SpaHostingTests
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
    public async Task Client_routes_return_the_spa(string path)
    {
        using var response = await s_client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "<div id=\"app\">");
    }

    [TestMethod]
    [DataRow("/api")]
    [DataRow("/api/nope")]
    [DataRow("/bff/whatever")]
    [DataRow("/hubs/events")]
    [DataRow("/mcp")]
    public async Task Reserved_paths_never_fall_back_to_the_spa(string path)
    {
        using var response = await s_client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Liveness_endpoint_reports_healthy()
    {
        using var response = await s_client.GetAsync(new Uri("/alive", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync());
    }
}
