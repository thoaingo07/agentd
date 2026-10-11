using System.Globalization;
using System.Threading.Channels;
using Agentd.Application.SystemLog;

namespace Agentd.Host.Logging;

/// <summary>
/// Stores the daemon's log (<see cref="SystemLogProvider"/>) in batches, off the logging thread: a bounded queue, so logging
/// never waits on the database. Entries that can't be kept (queue full, database unavailable, e.g. before setup) are
/// counted and reported by one warning once storing works again. It never logs itself (that would loop).
/// </summary>
public sealed class SystemLogWriter(IServiceProvider services) : BackgroundService
{
    public const int Capacity = 10_000;
    public const int MaxBatch = 500;
    public static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(1);

    private readonly Channel<NewSystemLogEntry> _queue = Channel.CreateBounded<NewSystemLogEntry>(
        new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private long _dropped;

    /// <summary>Entries lost so far and not yet reported.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public void Write(NewSystemLogEntry entry)
    {
        if (!_queue.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Stores what's queued now (also used on shutdown and by tests).</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var batch = new List<NewSystemLogEntry>(MaxBatch);
        while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var entry))
        {
            batch.Add(entry);
        }

        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
        {
            batch.Add(new NewSystemLogEntry(DateTimeOffset.UtcNow, LogLevel.Warning, typeof(SystemLogWriter).FullName!,
                string.Create(CultureInfo.InvariantCulture, $"{dropped} log entries weren't kept (the queue was full or the database was unavailable)."), null, null));
        }

        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await services.GetRequiredService<ISystemLogStore>().AddAsync(batch, ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the log must never take the daemon down; the loss is counted and reported
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            Interlocked.Add(ref _dropped, batch.Count - (dropped > 0 ? 1 : 0) + dropped);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(stoppingToken).ConfigureAwait(false))
            {
                await Task.Delay(FlushEvery, stoppingToken).ConfigureAwait(false);   // gather a batch
                while (_queue.Reader.Count > 0 || Dropped > 0)
                {
                    await FlushAsync(stoppingToken).ConfigureAwait(false);
                    if (Dropped > 0)
                    {
                        break;   // storing failed: try again with the next entries, not in a tight loop
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            using var last = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await FlushAsync(last.Token).ConfigureAwait(false);
        }
    }
}
