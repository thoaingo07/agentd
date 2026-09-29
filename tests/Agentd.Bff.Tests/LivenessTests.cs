using System.Net;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class LivenessTests
{
    [TestMethod]
    public async Task Liveness_endpoint_reports_healthy()
    {
        using var factory = new AgentdHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/alive", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("Healthy", await response.Content.ReadAsStringAsync());
    }
}
