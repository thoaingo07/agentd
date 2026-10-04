using System.Collections.Concurrent;
using System.Globalization;
using Agentd.Application.Events;
using Agentd.Application.Ports;
using Agentd.Bff.ViewModels;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Agentd.Bff.Hubs;

/// <summary>
/// One pump per (connection, stream). A pump subscribes to the live events <b>first</b>, then replays
/// the store after the client's <c>seq</c>, then streams live events it hasn't sent. Events commit in
/// <c>seq</c> order (see <c>events_serialize</c>), so "skip what is ≤ the last sent seq" never drops one.
/// If the live buffer overflows, the pump starts over from its last sent <c>seq</c>.
/// </summary>
public sealed partial class EventStreams(
    ILiveEvents live,
    IServiceScopeFactory scopes,
    IHubContext<EventsHub> hub,
    ILogger<EventStreams> logger) : IDisposable
{
    public const string AllStream = "all";
    public const int MaxStreamsPerConnection = 20;
    public const int ReplayPageSize = 500;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CancellationTokenSource>> _connections = new(StringComparer.Ordinal);

    /// <summary>Active pumps, for tests and diagnostics.</summary>
    public int ActiveStreams => _connections.Values.Sum(s => s.Count);

    public async Task SubscribeAsync(string connectionId, string stream, long afterSeq, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (afterSeq < 0)
        {
            throw new HubException("afterSeq can't be negative.");
        }

        long? jobId = null;
        if (stream != AllStream)
        {
            if (!long.TryParse(stream, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                throw new HubException($"Unknown stream '{stream}': use \"all\" or a job id.");
            }

            await using var scope = scopes.CreateAsyncScope();
            if (await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetAsync(new JobId(id), cancellationToken).ConfigureAwait(false) is null)
            {
                throw new HubException($"Job {id} was not found.");
            }

            jobId = id;
        }

        var streams = _connections.GetOrAdd(connectionId, _ => new(StringComparer.Ordinal));
        var pump = new CancellationTokenSource();
        lock (streams)
        {
            if (!streams.ContainsKey(stream) && streams.Count >= MaxStreamsPerConnection)
            {
                pump.Dispose();
                throw new HubException($"At most {MaxStreamsPerConnection} streams per connection.");
            }

            // Subscribing again (e.g. after a client-side reset) replaces the stream's pump.
            if (streams.TryRemove(stream, out var previous))
            {
                Stop(previous);
            }

            streams[stream] = pump;
        }

        _ = Task.Run(() => PumpAsync(connectionId, stream, jobId, afterSeq, pump.Token), CancellationToken.None);
    }

    public void Unsubscribe(string connectionId, string stream)
    {
        if (_connections.TryGetValue(connectionId, out var streams) && streams.TryRemove(stream, out var pump))
        {
            Stop(pump);
        }
    }

    public void RemoveConnection(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var streams))
        {
            foreach (var pump in streams.Values)
            {
                Stop(pump);
            }
        }
    }

    public void Dispose()
    {
        foreach (var connection in _connections.Keys.ToList())
        {
            RemoveConnection(connection);
        }
    }

    private async Task PumpAsync(string connectionId, string stream, long? jobId, long afterSeq, CancellationToken ct)
    {
        var client = hub.Clients.Client(connectionId);
        var last = afterSeq;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var events = live.Subscribe(jobId, ct);   // subscribed now; replay below can't miss anything
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var reader = scope.ServiceProvider.GetRequiredService<IEventReader>();
                    IReadOnlyList<AgentEventDto> page;
                    do
                    {
                        page = await reader.ReadAfterAsync(jobId is { } id ? new JobId(id) : null, last, ReplayPageSize, ct).ConfigureAwait(false);
                        foreach (var evt in page)
                        {
                            await client.SendAsync(EventsHub.EventMethod, stream, EventVm.ForStream(evt), ct).ConfigureAwait(false);
                            last = evt.Seq;
                        }
                    }
                    while (page.Count == ReplayPageSize);
                }

                await foreach (var evt in events.ConfigureAwait(false))
                {
                    if (evt.Seq > last)
                    {
                        await client.SendAsync(EventsHub.EventMethod, stream, EventVm.ForStream(evt), ct).ConfigureAwait(false);
                        last = evt.Seq;
                    }
                }
            }
            catch (LiveEventsOverflowException)
            {
                LogResuming(logger, stream, last);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // a pump must survive store hiccups; it retries from its last seq
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogPumpFailed(logger, ex, stream, last);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private static void Stop(CancellationTokenSource pump)
    {
        pump.Cancel();
        pump.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Event stream {Stream} fell behind; replaying from seq {Seq}")]
    private static partial void LogResuming(ILogger logger, string stream, long seq);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Event stream {Stream} failed at seq {Seq}; retrying")]
    private static partial void LogPumpFailed(ILogger logger, Exception exception, string stream, long seq);
}
