using System.Globalization;
using Agentd.Application.Events;
using Agentd.Application.Ports;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentd.Infrastructure.Persistence.Events;

/// <summary>
/// Streams committed events live: <c>LISTEN agentd_events</c> on a dedicated connection; each
/// notification carries a <c>seq</c> (sent by the insert trigger, delivered only on commit), which is
/// loaded from the store and published to <see cref="EventHub"/>. Duplicates are ignored by seq; the
/// connection is re-opened with back-off when it drops.
/// </summary>
public sealed partial class EventNotificationListener(
    NpgsqlDataSource dataSource,
    IEventReader store,
    EventHub hub,
    ILogger<EventNotificationListener> logger) : BackgroundService
{
    public const string Channel = "agentd_events";
    private const int RememberedSeqs = 4096;

    private readonly HashSet<long> _seen = [];
    private readonly Queue<long> _order = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await dataSource.OpenConnectionAsync(stoppingToken).ConfigureAwait(false);
                var pending = new Queue<long>();
                connection.Notification += (_, e) =>
                {
                    if (long.TryParse(e.Payload, NumberStyles.None, CultureInfo.InvariantCulture, out var seq))
                    {
                        pending.Enqueue(seq);
                    }
                };
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken).ConfigureAwait(false);
                }

                LogListening(logger);
                delay = TimeSpan.FromSeconds(1);
                while (!stoppingToken.IsCancellationRequested)
                {
                    await connection.WaitAsync(stoppingToken).ConfigureAwait(false);
                    while (pending.TryDequeue(out var seq))
                    {
                        await DeliverAsync(seq, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is NpgsqlException or System.IO.IOException or InvalidOperationException)
            {
                LogReconnecting(logger, ex, delay);
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    /// <summary>Loads and publishes one notified event, once per seq.</summary>
    internal async Task DeliverAsync(long seq, CancellationToken ct)
    {
        if (!_seen.Add(seq))
        {
            return;
        }

        _order.Enqueue(seq);
        if (_order.Count > RememberedSeqs)
        {
            _seen.Remove(_order.Dequeue());
        }

        if (await store.GetAsync(seq, ct).ConfigureAwait(false) is { } evt)
        {
            hub.Publish(evt);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for committed events")]
    private static partial void LogListening(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event listener connection lost; reconnecting in {Delay}")]
    private static partial void LogReconnecting(ILogger logger, Exception exception, TimeSpan delay);
}

/// <summary>Creates next month's <c>events</c> partition ahead of time: at startup and then daily.</summary>
public sealed partial class EventPartitionMaintenance(NpgsqlDataSource dataSource, ILogger<EventPartitionMaintenance> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
        do
        {
            try
            {
                await using var cmd = dataSource.CreateCommand("SELECT agentd.event_ensure_partitions($1)");
                cmd.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
                var created = (int)(await cmd.ExecuteScalarAsync(stoppingToken).ConfigureAwait(false))!;
                if (created > 0)
                {
                    LogCreated(logger, created);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created {Count} event partition(s)")]
    private static partial void LogCreated(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Creating event partitions failed; rows go to the default partition until it succeeds")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
