using System.Net;
using System.Text.Json;
using Agentd.Application.SystemLog;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class SystemLogEndpointTests
{
    private readonly Store _store = new();

    [TestMethod]
    public async Task Admins_page_through_the_log_newest_first_and_others_are_refused()
    {
        await using var app = await StartAsync();

        using var page = await GetAsync(app, "/api/system-log?limit=2&level=warning&category=monitor&q=failed", "thoaingo07@gmail.com");
        using var next = await GetAsync(app, "/api/system-log?before=2", "thoaingo07@gmail.com");
        using var bad = await GetAsync(app, "/api/system-log?level=loud", "thoaingo07@gmail.com");
        using var other = await GetAsync(app, "/api/system-log", "dev@example.com");

        var body = JsonDocument.Parse(await page.Content.ReadAsStringAsync()).RootElement;
        CollectionAssert.AreEqual(new[] { "id", "at", "level", "category", "message", "exception", "jobId" }, body.GetProperty("entries")[0].EnumerateObject().Select(p => p.Name).ToArray(), "the TS contract");
        Assert.AreEqual((2, true, "Warning"), (body.GetProperty("entries").GetArrayLength(), body.GetProperty("hasMore").GetBoolean(), body.GetProperty("entries")[0].GetProperty("level").GetString()));
        Assert.AreEqual(((long?)null, LogLevel.Warning, "monitor", "failed", 3), _store.Asked[0], "one more than the page, to know there's more");
        Assert.AreEqual(((long?)2, LogLevel.Information, (string?)null, (string?)null, 101), _store.Asked[1]);
        Assert.AreEqual((HttpStatusCode.OK, HttpStatusCode.BadRequest, HttpStatusCode.Forbidden), (next.StatusCode, bad.StatusCode, other.StatusCode));
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
        builder.Configuration["Agentd:Auth:Tailscale:AdminLogins:0"] = "thoaingo07@gmail.com";
        builder.Services.AddBff();
        builder.Services.AddSingleton<ISystemLogStore>(_store);
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

    private sealed class Store : ISystemLogStore
    {
        public List<(long? Before, LogLevel Min, string? Category, string? Search, int Limit)> Asked { get; } = [];

        public Task<IReadOnlyList<SystemLogEntry>> ListAsync(long? beforeId, LogLevel minLevel, string? category, string? search, int limit, CancellationToken cancellationToken)
        {
            Asked.Add((beforeId, minLevel, category, search, limit));
            return Task.FromResult<IReadOnlyList<SystemLogEntry>>([.. Enumerable.Range(0, 3).Select(i =>
                new SystemLogEntry(3 - i, DateTimeOffset.UnixEpoch, LogLevel.Warning, "Agentd.Application.Monitor.PrMonitor", $"Checking watched PR failed {i}", null, 42))]);
        }

        public Task<int> AddAsync(IReadOnlyList<NewSystemLogEntry> entries, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
