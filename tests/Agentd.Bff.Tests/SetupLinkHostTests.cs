using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Agentd.Bff.Tests;

/// <summary>The real daemon: it logs the setup link at start, and the token shows up in no other log line.</summary>
[TestClass]
public sealed partial class SetupLinkHostTests
{
    [TestMethod]
    [DoNotParallelize]   // the daemon's home (and its setup token) is shared by every AgentdHostFactory
    public async Task The_daemon_prints_the_link_and_never_logs_the_token_otherwise()
    {
        var logs = new LogCapture();
        // Development: the most verbose levels (Debug, ASP.NET Core at Information); no line but the announcement may carry the token.
        using var factory = new AgentdHostFactory(environment: "Development").WithWebHostBuilder(b => b.ConfigureLogging(l => l.AddProvider(logs)));
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var announcement = logs.Lines.Single(l => l.Contains("isn't set up yet", StringComparison.Ordinal));
        var token = Uri.UnescapeDataString(Token().Match(announcement).Groups[1].Value);
        using var response = await client.GetAsync(new Uri($"/setup?token={Uri.EscapeDataString(token)}", UriKind.Relative));

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode, announcement);
        Assert.AreEqual(1, logs.Lines.Count(l => l.Contains(token, StringComparison.Ordinal) || l.Contains(Uri.EscapeDataString(token), StringComparison.Ordinal)),
            string.Join('\n', logs.Lines.Where(l => l.Contains("setup", StringComparison.OrdinalIgnoreCase))));
    }

    [GeneratedRegex(@"/setup\?token=([A-Za-z0-9_%\-]+)")]
    private static partial Regex Token();

    private sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyList<string> Lines => [.. _lines];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category}: {formatter(state, exception)}");
        }
    }
}
