using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class TestSeedEndpointTests
{
    [TestMethod]
    [DataRow("Production", HttpStatusCode.NotFound)]
    [DataRow("e2e-like", HttpStatusCode.NotFound)]
    [DataRow("Staging", HttpStatusCode.NotFound)]
    [DataRow("E2E", HttpStatusCode.OK)]
    public async Task Test_seeding_is_mapped_only_in_the_E2E_environment(string environment, HttpStatusCode expected)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/__test/csp-reports", UriKind.Relative));

        Assert.AreEqual(expected, response.StatusCode);
        Assert.AreEqual(environment == "E2E", Testing.TestSeedEndpoints.ShouldMap(app.Environment));
    }

    [TestMethod]
    public void Development_never_maps_test_seeding()
    {
        // (Development validates the whole container on build, so it's checked here rather than by starting a host.)
        Assert.IsFalse(Testing.TestSeedEndpoints.ShouldMap(new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" }));
    }
}
