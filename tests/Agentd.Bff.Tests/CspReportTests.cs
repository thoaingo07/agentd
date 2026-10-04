using System.Collections.Concurrent;
using System.Net;
using Agentd.Application.Events;
using Agentd.Bff.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class CspReportTests
{
    [TestMethod]
    public async Task Both_report_formats_are_logged_once_each()
    {
        var logs = new Logs();
        await using var app = await StartAsync(logs);
        using var client = app.GetTestClient();

        using var modern = await PostAsync(client, """[{"type":"csp-violation","body":{"effectiveDirective":"style-src-elem","blockedURL":"inline","documentURL":"http://127.0.0.1:7780/jobs/1","disposition":"enforce"}}]""", "application/reports+json");
        using var legacy = await PostAsync(client, """{"csp-report":{"violated-directive":"script-src","blocked-uri":"eval","document-uri":"http://127.0.0.1:7780/","disposition":"report"}}""", "application/csp-report");

        Assert.AreEqual(HttpStatusCode.NoContent, modern.StatusCode);
        Assert.AreEqual(HttpStatusCode.NoContent, legacy.StatusCode);
        CollectionAssert.AreEqual(
            new[]
            {
                "CSP violation: style-src-elem blocked inline on http://127.0.0.1:7780/jobs/1 (enforce)",
                "CSP violation: script-src blocked eval on http://127.0.0.1:7780/ (report)",
            },
            logs.Warnings.ToArray());
    }

    [TestMethod]
    public async Task Big_bodies_are_refused_and_floods_are_rate_limited()
    {
        await using var app = await StartAsync(new Logs());
        using var client = app.GetTestClient();

        using var big = await PostAsync(client, "[" + new string(' ', 10 * 1024) + "]", "application/reports+json");
        Assert.AreEqual(HttpStatusCode.RequestEntityTooLarge, big.StatusCode);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < CspReportEndpoint.PermitsPerMinute + 1; i++)
        {
            using var response = await PostAsync(client, "[]", "application/reports+json");
            statuses.Add(response.StatusCode);
        }

        Assert.AreEqual(HttpStatusCode.TooManyRequests, statuses[^1]);
        Assert.IsTrue(statuses.Take(CspReportEndpoint.PermitsPerMinute - 1).All(s => s == HttpStatusCode.NoContent), "the big body used one permit");
    }

    [TestMethod]
    public void Garbage_is_ignored()
    {
        Assert.IsEmpty(CspReportEndpoint.Parse("not json"));
        Assert.IsEmpty(CspReportEndpoint.Parse("""{"other":1}"""));
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string json, string contentType)
    {
        using var body = new StringContent(json);
        body.Headers.ContentType = new(contentType);
        return await client.PostAsync(new Uri("/api/csp-report", UriKind.Relative), body);
    }

    private static async Task<WebApplication> StartAsync(Logs logs)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(logs);
        builder.Services.AddBff();
        builder.Services.AddSingleton<ILiveEvents>(new EventHub(NullLogger<EventHub>.Instance));
        var app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Logs : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();

        public ILogger CreateLogger(string categoryName) => categoryName == typeof(CspReportEndpoint).FullName ? this : NullLogger.Instance;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Enqueue(formatter(state, exception));
            }
        }

        public void Dispose()
        {
        }
    }
}
