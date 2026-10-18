using System.Net;
using System.Text.Json;
using Agentd.Application.Incidents;
using Agentd.Bff.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class IncidentEndpointTests
{
    private const string Admin = "thoaingo07@gmail.com";
    private const string Other = "dev@example.com";
    private const string OrdersId = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Insights/components/orders-ai";
    private readonly Store _store = new();
    private readonly Telemetry _telemetry = new();

    [TestMethod]
    public async Task The_list_filters_by_status_caps_the_limit_and_refuses_an_unknown_status()
    {
        await using var app = await StartAsync();

        using var all = await GetAsync(app, "/api/incidents?limit=9999", Other);
        using var open = await GetAsync(app, "/api/incidents?status=Open", Other);
        using var bad = await GetAsync(app, "/api/incidents?status=Gone", Other);

        var body = JsonDocument.Parse(await all.Content.ReadAsStringAsync()).RootElement;
        CollectionAssert.AreEqual(
            new[] { "id", "app", "kind", "title", "message", "operation", "status", "totalCount", "firstSeen", "lastSeen", "workItemId", "analyzed" },
            body[0].EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        Assert.AreEqual((7L, "orders-api", true, 9001), (body[0].GetProperty("id").GetInt64(), body[0].GetProperty("app").GetString(),
            body[0].GetProperty("analyzed").GetBoolean(), body[0].GetProperty("workItemId").GetInt32()));
        CollectionAssert.AreEqual(new (string?, int)[] { (null, IncidentEndpoints.MaxIncidents), ("Open", 200) }, _store.Listed);
        Assert.AreEqual((HttpStatusCode.OK, HttpStatusCode.BadRequest), (open.StatusCode, bad.StatusCode));
    }

    [TestMethod]
    public async Task An_incident_comes_with_its_redacted_sample_analysis_repository_and_portal_link()
    {
        await using var app = await StartAsync();

        using var found = await GetAsync(app, "/api/incidents/7", Other);
        using var missing = await GetAsync(app, "/api/incidents/8", Other);

        var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual(("NullReferenceException", "sysmin", true), (body.GetProperty("summary").GetProperty("title").GetString(), body.GetProperty("repository").GetString(),
            body.GetProperty("hasThread").GetBoolean()));
        Assert.AreEqual($"https://portal.azure.com/#resource{OrdersId}/failures", body.GetProperty("portalUrl").GetString());
        StringAssert.Contains(body.GetProperty("stack").GetString(), "at Orders.Get()");
        Assert.IsFalse(body.GetProperty("stack").GetString()!.Contains("hunter2", StringComparison.Ordinal), "redacted");
        Assert.AreEqual("**Likely cause** `src/Orders.cs:42`", body.GetProperty("analysis").GetString());
        Assert.AreEqual(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [TestMethod]
    public async Task Occurrences_are_read_live_redacted_for_Operators_and_Admins_and_a_telemetry_failure_is_a_502()
    {
        await using var app = await StartAsync();

        using var found = await GetAsync(app, "/api/incidents/7/occurrences", Admin);
        using var other = await GetAsync(app, "/api/incidents/7/occurrences", Other);
        _telemetry.Fail = true;
        using var failed = await GetAsync(app, "/api/incidents/7/occurrences", Admin);

        var body = JsonDocument.Parse(await found.Content.ReadAsStringAsync()).RootElement;
        Assert.AreEqual(("/orders?id=1", "500"), (body[0].GetProperty("url").GetString(), body[0].GetProperty("resultCode").GetString()));
        Assert.IsFalse(body[0].GetProperty("properties").GetString()!.Contains("hunter2", StringComparison.Ordinal), "redacted");
        Assert.AreEqual((7L, IncidentEndpoints.MaxOccurrences), _telemetry.Asked.Single(), "one Azure read, for Operators and Admins only");
        Assert.AreEqual((HttpStatusCode.Forbidden, HttpStatusCode.BadGateway), (other.StatusCode, failed.StatusCode));
    }

    private static async Task<HttpResponseMessage> GetAsync(WebApplication app, string url, string login)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Add("Tailscale-User-Login", login);
        request.Headers.Add("X-Forwarded-For", "100.111.15.109");
        return await app.GetTestClient().SendAsync(request);
    }

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Agentd:Auth:Mode"] = "Tailscale";
        builder.Configuration["Agentd:Auth:Tailscale:AdminLogins:0"] = Admin;
        builder.Services.AddBff();
        builder.Services.AddSingleton<IIncidentStore>(_store);
        builder.Services.AddSingleton<ITelemetrySource>(_telemetry);
        builder.Services.Configure<IncidentMonitorOptions>(o => o.Apps.Add(new MonitoredApp { Name = "orders-api", ResourceId = OrdersId, Repository = "sysmin" }));
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;   // Serve connects from this machine
            return next(context);
        });
        app.UseBffForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Store : IIncidentStore
    {
        private static readonly Incident s_incident = new(7, "orders-api", "ex|NRE|GET /orders", ErrorKinds.Exception, "NullReferenceException", "Object reference not set",
            "GET /orders", new ErrorSample("at Orders.Get()\nPassword=hunter2;", null, "op1"), IncidentStatus.Fixing, 42, DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddDays(30), "discord", "t1", null, 9001, DateTimeOffset.UnixEpoch, "**Likely cause** `src/Orders.cs:42`");

        public List<(string? Status, int Limit)> Listed { get; } = [];

        public Task<IReadOnlyList<Incident>> ListAsync(string? status, int limit, CancellationToken cancellationToken)
        {
            Listed.Add((status, limit));
            return Task.FromResult<IReadOnlyList<Incident>>([s_incident]);
        }

        public Task<Incident?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult(id == 7 ? s_incident : null);

        public Task<IncidentRecorded?> RecordAsync(string app, ErrorGroup group, int minCount, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetThreadAsync(long id, string provider, string threadId, string? spaceId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> SetStatusAsync(long id, string status, string? from, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetAnalysisAsync(long id, string analysis, Guid session, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int?> SetWorkItemAsync(long id, int workItemId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Incident?> FindByThreadAsync(string provider, string threadId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DateTimeOffset?> GetCursorAsync(string app, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetCursorAsync(string app, DateTimeOffset checkedUntil, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Telemetry : ITelemetrySource
    {
        public bool Fail { get; set; }

        public List<(long Id, int Max)> Asked { get; } = [];

        public Task<IReadOnlyList<ErrorGroup>> ErrorsAsync(MonitoredApp app, DateTimeOffset since, DateTimeOffset until, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ErrorOccurrence>> OccurrencesAsync(MonitoredApp app, Incident incident, DateTimeOffset since, int max, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new InvalidOperationException("Forbidden");
            }

            Asked.Add((incident.Id, max));
            return Task.FromResult<IReadOnlyList<ErrorOccurrence>>([new(DateTimeOffset.UnixEpoch, "boom", "at Orders.Get()", "GET /orders", "op1", "/orders?id=1", "500",
                """{"conn":"Password=hunter2;"}""")]);
        }
    }
}
