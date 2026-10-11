using Agentd.Application.SystemLog;
using Agentd.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.Logging;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class SystemLogStoreTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Parallel_batches_all_land_and_the_list_filters_and_pages_newest_first()
    {
        await using var db = await Database.CreateMigratedAsync("system_log_list");
        var store = new SystemLogStore(db);

        var added = await Task.WhenAll(Enumerable.Range(0, 8).Select(b => Task.Run(() => store.AddAsync(
            [.. Enumerable.Range(0, 25).Select(i => new NewSystemLogEntry(s_now, LogLevel.Information, "Agentd.Host.Workers.SchedulerWorker", $"batch {b} entry {i}", null, null))], default))));
        await store.AddAsync(
        [
            new(s_now, LogLevel.Warning, "Agentd.Application.Monitor.PrMonitor", "Checking watched PR 3944 failed", "System.TimeoutException: slow", 42),
            new(s_now, LogLevel.Error, "Microsoft.AspNetCore.Server.Kestrel", "Connection reset", null, null),
        ], default);

        Assert.AreEqual(200, added.Sum());
        var newest = await store.ListAsync(null, LogLevel.Information, null, null, 5, default);
        Assert.AreEqual("Connection reset", newest[0].Message);
        Assert.IsTrue(newest.Zip(newest.Skip(1)).All(p => p.First.Id > p.Second.Id), "newest first");
        var older = await store.ListAsync(newest[^1].Id, LogLevel.Information, null, null, 500, default);
        Assert.HasCount(202 - 5, older, "paging by id reaches everything else");

        var warnings = await store.ListAsync(null, LogLevel.Warning, null, null, 50, default);
        CollectionAssert.AreEqual(new[] { LogLevel.Error, LogLevel.Warning }, warnings.Select(e => e.Level).ToArray());
        var monitor = (await store.ListAsync(null, LogLevel.Trace, "prmonitor", null, 50, default)).Single();
        Assert.AreEqual(((long?)42, "System.TimeoutException: slow"), (monitor.JobId, monitor.Exception));
        Assert.HasCount(1, await store.ListAsync(null, LogLevel.Trace, null, "TIMEOUT", 50, default), "the search covers the exception, case-insensitive");
        Assert.HasCount(25, await store.ListAsync(null, LogLevel.Trace, "scheduler", "batch 3 ", 50, default));
    }

    [TestMethod]
    public async Task Retention_deletes_only_older_entries_once_even_with_parallel_sweeps()
    {
        await using var db = await Database.CreateMigratedAsync("system_log_retention");
        var store = new SystemLogStore(db);
        await store.AddAsync(
        [
            new(s_now.AddDays(-20), LogLevel.Information, "Agentd.X", "old", null, null),
            new(s_now.AddDays(-15), LogLevel.Information, "Agentd.X", "older than 14 days", null, null),
            new(s_now.AddDays(-1), LogLevel.Information, "Agentd.X", "recent", null, null),
        ], default);

        var deleted = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Task.Run(() => store.DeleteBeforeAsync(s_now.AddDays(-14), default))));

        Assert.AreEqual(2, deleted.Sum());
        Assert.AreEqual("recent", (await store.ListAsync(null, LogLevel.Trace, null, null, 10, default)).Single().Message);
        Assert.AreEqual(0, await store.AddAsync([], default));
    }
}
