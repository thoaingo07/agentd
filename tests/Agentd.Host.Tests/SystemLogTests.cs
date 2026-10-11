using Agentd.Application.SystemLog;
using Agentd.Host.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

#pragma warning disable CA1873, CA1848 // the tests log through ILogger like any library does

namespace Agentd.Host.Tests;

[TestClass]
public sealed class SystemLogTests
{
    private readonly Store _store = new();
    private readonly SystemLogOptions _options = new();

    [TestMethod]
    public async Task Agentds_information_and_everyones_warnings_are_kept_redacted_and_linked_to_their_job()
    {
        var (writer, factory) = Start();

        factory.CreateLogger("Agentd.Application.Jobs.Scheduler").LogInformation("Job {JobId} claimed with Bearer abcdefghijklmnopqrstuvwxyz0123", 42L);
        factory.CreateLogger("Agentd.Application.Jobs.Scheduler").LogDebug("too chatty");
        factory.CreateLogger("Microsoft.Hosting.Lifetime").LogInformation("Application started");
        factory.CreateLogger("Microsoft.AspNetCore.Server.Kestrel").LogWarning("Connection reset");
        factory.CreateLogger("Npgsql.Command").LogError("the database is down");
        factory.CreateLogger(typeof(SystemLogWriter).FullName!).LogError("storing failed");
        factory.CreateLogger("Agentd.Host.Workers.X").LogError(new InvalidOperationException("token ghp_abcdefghijklmnopqrstuvwxyz0123"), "Pass failed");
        await writer.FlushAsync(default);

        CollectionAssert.AreEqual(new[] { "Job 42 claimed with Bearer «redacted»", "Connection reset", "Pass failed" }, _store.Added.Select(e => e.Message).ToArray(),
            "agentd's Information, others' Warning; Npgsql's and the writer's own never (no loops)");
        Assert.AreEqual((long?)42, _store.Added[0].JobId);
        StringAssert.Contains(_store.Added[2].Exception, "token «redacted»");
        Assert.IsNull(_store.Added[1].JobId);
    }

    [TestMethod]
    public async Task Losses_are_counted_and_reported_once_storing_works_and_disabled_keeps_nothing()
    {
        var (writer, factory) = Start();
        var log = factory.CreateLogger("Agentd.Application.Jobs.Scheduler");
        _store.Failing = true;

        log.LogInformation("one");
        log.LogInformation("two");
        await writer.FlushAsync(default);
        _store.Failing = false;
        log.LogInformation("three");
        await writer.FlushAsync(default);
        _options.Enabled = false;
        log.LogError("not kept");
        await writer.FlushAsync(default);

        Assert.AreEqual(2L, _store.Added.Count, "\"three\" and the report");
        Assert.AreEqual("three", _store.Added[0].Message);
        Assert.AreEqual((LogLevel.Warning, "2 log entries weren't kept (the queue was full or the database was unavailable)."), (_store.Added[1].Level, _store.Added[1].Message));
        Assert.AreEqual(0L, writer.Dropped);
    }

    private (SystemLogWriter Writer, ILoggerFactory Factory) Start()
    {
        var services = new ServiceCollection().AddSingleton<ISystemLogStore>(_store).BuildServiceProvider();
        var writer = new SystemLogWriter(services);
        var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(new SystemLogProvider(writer, new Monitor(_options))));
        return (writer, factory);
    }

    private sealed class Monitor(SystemLogOptions value) : IOptionsMonitor<SystemLogOptions>
    {
        public SystemLogOptions CurrentValue => value;

        public SystemLogOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<SystemLogOptions, string?> listener) => null;
    }

    private sealed class Store : ISystemLogStore
    {
        public List<NewSystemLogEntry> Added { get; } = [];

        public bool Failing { get; set; }

        public Task<int> AddAsync(IReadOnlyList<NewSystemLogEntry> entries, CancellationToken cancellationToken)
        {
            if (Failing)
            {
                throw new InvalidOperationException("database unavailable");
            }

            Added.AddRange(entries);
            return Task.FromResult(entries.Count);
        }

        public Task<IReadOnlyList<SystemLogEntry>> ListAsync(long? beforeId, LogLevel minLevel, string? category, string? search, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
