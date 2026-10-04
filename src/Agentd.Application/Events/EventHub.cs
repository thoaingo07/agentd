using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Agentd.Application.Events;

/// <summary>One event, as streamed to the UI.</summary>
public sealed record AgentEventDto(long Seq, long? JobId, DateTimeOffset Ts, string Type, JsonElement Payload)
{
    /// <summary>Summary types go to the "all jobs" stream; the agent's raw output (<c>agent.*</c>) only to its job's stream.</summary>
    public bool IsSummary => !Type.StartsWith("agent.", StringComparison.Ordinal);
}

/// <summary>Live events for one job, or for all jobs (summary types only).</summary>
public interface ILiveEvents
{
    /// <summary>
    /// Committed events as they arrive, in <c>seq</c> order. The subscription starts when this method is
    /// called (not when enumeration starts), so a caller can subscribe, read the store, then stream
    /// without missing an event in between. A subscriber that falls behind gets
    /// <see cref="LiveEventsOverflowException"/> once its buffer is drained; it catches up by reading
    /// the store from its last <c>seq</c>. Cancel <paramref name="cancellationToken"/> to unsubscribe.
    /// </summary>
    IAsyncEnumerable<AgentEventDto> Subscribe(long? jobId, CancellationToken cancellationToken);
}

/// <summary>A live subscriber fell behind and events were dropped; resubscribe and read the store from the last <c>seq</c>.</summary>
public sealed class LiveEventsOverflowException : Exception
{
    public LiveEventsOverflowException()
        : base("The live event buffer overflowed; read the store from the last seq.")
    {
    }

    public LiveEventsOverflowException(string message)
        : base(message)
    {
    }

    public LiveEventsOverflowException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// In-process fan-out of committed events (fed by the PostgreSQL listener). Each subscriber has a
/// bounded buffer of <see cref="BufferSize"/>; when it is full the subscription ends with
/// <see cref="LiveEventsOverflowException"/> instead of silently skipping events.
/// </summary>
public sealed partial class EventHub(ILogger<EventHub> logger) : ILiveEvents
{
    public const int BufferSize = 1000;

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    public void Publish(AgentEventDto evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        foreach (var subscriber in _subscribers.Values)
        {
            if ((subscriber.JobId is null && evt.IsSummary) || subscriber.JobId == evt.JobId)
            {
                subscriber.Write(evt);
            }
        }
    }

    public IAsyncEnumerable<AgentEventDto> Subscribe(long? jobId, CancellationToken cancellationToken)
    {
        // Registered now, before any enumeration, so nothing published after this call is missed.
        var id = Guid.NewGuid();
        var subscriber = new Subscriber(jobId);
        _subscribers[id] = subscriber;
        var unsubscribe = cancellationToken.Register(() => _subscribers.TryRemove(id, out _));
        return ReadAsync(id, subscriber, unsubscribe, cancellationToken);
    }

    private async IAsyncEnumerable<AgentEventDto> ReadAsync(Guid id, Subscriber subscriber, CancellationTokenRegistration unsubscribe, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var evt in subscriber.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }

            if (subscriber.Overflowed)
            {
                LogDropped(logger, subscriber.JobId);
                throw new LiveEventsOverflowException();
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            await unsubscribe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class Subscriber(long? jobId)
    {
        private readonly Channel<AgentEventDto> _channel = Channel.CreateBounded<AgentEventDto>(
            new BoundedChannelOptions(BufferSize) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });

        public long? JobId { get; } = jobId;

        private volatile bool _overflowed;

        public bool Overflowed => _overflowed;

        public ChannelReader<AgentEventDto> Reader => _channel.Reader;

        /// <summary>Never blocks the publisher: a full buffer completes the channel, and the reader then overflows.</summary>
        public void Write(AgentEventDto evt)
        {
            if (!_channel.Writer.TryWrite(evt) && !_overflowed)
            {
                _overflowed = true;
                _channel.Writer.TryComplete();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live event subscriber (job {JobId}) fell behind; it resumes from the store")]
    private static partial void LogDropped(ILogger logger, long? jobId);
}
