using System.Net;
using System.Text.Json;
using Agentd.Application.Setup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class RestartEndpointTests
{
    [TestMethod]
    public async Task An_admin_restarts_a_supervised_agentd()
    {
        var daemon = new Daemon(available: true);
        await using var app = await StartAsync(daemon);
        var client = await new AntiforgeryClient(app).InitAsync();

        var status = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(new Uri("/api/settings/restart", UriKind.Relative))).RootElement;
        using var noToken = await client.PostAsync("/api/settings/restart", token: "wrong");
        using var restart = await client.PostAsync("/api/settings/restart");

        Assert.AreEqual((true, JsonValueKind.Null), (status.GetProperty("available").GetBoolean(), status.GetProperty("reason").ValueKind));
        Assert.AreEqual((HttpStatusCode.BadRequest, HttpStatusCode.Accepted), (noToken.StatusCode, restart.StatusCode));
        CollectionAssert.AreEqual(new[] { "local" }, daemon.By, "once, by who asked");
    }

    [TestMethod]
    public async Task Without_a_supervisor_it_says_why_and_doesnt_stop()
    {
        var daemon = new Daemon(available: false);
        await using var app = await StartAsync(daemon);
        var client = await new AntiforgeryClient(app).InitAsync();

        var status = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(new Uri("/api/settings/restart", UriKind.Relative))).RootElement;
        using var restart = await client.PostAsync("/api/settings/restart");

        Assert.AreEqual((false, "no supervisor"), (status.GetProperty("available").GetBoolean(), status.GetProperty("reason").GetString()));
        Assert.AreEqual(HttpStatusCode.Conflict, restart.StatusCode);
        StringAssert.Contains(await restart.Content.ReadAsStringAsync(), "no supervisor");
        Assert.IsEmpty(daemon.By);
    }

    private static async Task<WebApplication> StartAsync(IDaemonRestart daemon)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddBff();
        builder.Services.AddSingleton(daemon);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Daemon(bool available) : IDaemonRestart
    {
        public List<string> By { get; } = [];

        public RestartAvailability Availability => new(available, available ? null : "no supervisor");

        public Task<bool> RequestAsync(string by, CancellationToken cancellationToken)
        {
            if (available)
            {
                By.Add(by);
            }

            return Task.FromResult(available);
        }
    }
}
