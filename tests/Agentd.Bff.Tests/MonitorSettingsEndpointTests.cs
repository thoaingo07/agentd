using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agentd.Application.Incidents;
using Agentd.Application.Setup;
using Agentd.Bff.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class MonitorSettingsEndpointTests
{
    private const string OrdersId = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Insights/components/orders-ai";
    private readonly Config _config = new();

    [TestMethod]
    public async Task Admins_read_save_and_test_the_monitor_settings()
    {
        await using var app = await StartAsync();
        var client = await new AntiforgeryClient(app).InitAsync();
        var orders = new { name = "orders-api", resourceId = OrdersId, repository = "sysmin", roleName = (string?)null };

        using var saved = await client.SendAsync(HttpMethod.Put, "/api/settings/monitor", new { enabled = true, tenantId = "t1", apps = new[] { orders } });
        using var bad = await client.SendAsync(HttpMethod.Put, "/api/settings/monitor", new { enabled = true, apps = Array.Empty<object>() });
        using var tooMany = await client.SendAsync(HttpMethod.Put, "/api/settings/monitor", new { enabled = false, apps = Enumerable.Repeat(orders, MonitorSettingsEndpoints.MaxApps + 1) });
        using var tested = await client.PostAsync("/api/settings/monitor/test", orders);
        var step = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(new Uri("/api/settings/monitor", UriKind.Relative))).RootElement;

        Assert.IsTrue((await saved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("restartRequired").GetBoolean());
        Assert.AreEqual((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (bad.StatusCode, tooMany.StatusCode));
        Assert.AreEqual(("orders-api", true, false), (step.GetProperty("apps")[0].GetProperty("name").GetString(), step.GetProperty("enabled").GetBoolean(),
            step.GetProperty("running").GetBoolean()), "saved, but the running monitor changes after a restart");
        Assert.AreEqual("Read orders-api: no exceptions or failed requests in the last hour.", (await tested.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("message").GetString());
    }

    [TestMethod]
    public async Task Only_Admins_may_see_them()
    {
        await using var app = await StartAsync(admin: false);

        using var response = await app.GetTestClient().GetAsync(new Uri("/api/settings/monitor", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<WebApplication> StartAsync(bool admin = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        if (!admin)
        {
            builder.Configuration["Agentd:Auth:Mode"] = "Tailscale";
            builder.Configuration["Agentd:Auth:Tailscale:AdminLogins:0"] = "someone-else@example.com";
        }

        builder.Services.AddBff();
        builder.Services.AddSingleton<IConfigWriter>(_config);
        builder.Services.AddSingleton<ISettingsAudit, Audit>();
        builder.Services.AddSingleton<ITelemetrySource, Telemetry>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<MonitorSettings>();
        var app = builder.Build();
        if (!admin)
        {
            app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;   // Serve connects from this machine
                context.Request.Headers["Tailscale-User-Login"] = "dev@example.com";
                context.Request.Headers["X-Forwarded-For"] = "100.111.15.109";
                return next(context);
            });
            app.UseBffForwardedHeaders();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Config : IConfigWriter
    {
        private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);

        public string? Read(string key) => _values.GetValueOrDefault(key);

        public IReadOnlyList<string> Children(string section) => [];

        public Task SetAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
        {
            foreach (var (k, v) in values)
            {
                _values[k] = v;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Audit : ISettingsAudit
    {
        public Task RecordAsync(IReadOnlyList<SettingsChange> changes, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class Telemetry : ITelemetrySource
    {
        public Task<IReadOnlyList<ErrorGroup>> ErrorsAsync(MonitoredApp app, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ErrorGroup>>([]);

        public Task<IReadOnlyList<ErrorOccurrence>> OccurrencesAsync(MonitoredApp app, Incident incident, DateTimeOffset since, int max, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
