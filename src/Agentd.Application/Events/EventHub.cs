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
    /// Committed events as they arrive. A subscriber that falls behind loses the oldest buffered events
    /// (a warning is logged); it catches up by reading the store from its last <c>seq</c>.
    /// </summary>
    IAsyncEnumerable<AgentEventDto> Subscribe(long? jobId, CancellationToken cancellationToken);
}

/// <summary>
/// In-process fan-out of committed events (fed by the PostgreSQL listener). Each subscriber has a
/// bounded buffer of <see cref="BufferSize"/> that drops the oldest event when full.
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

    public async IAsyncEnumerable<AgentEventDto> Subscribe(long? jobId, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var subscriber = new Subscriber(jobId, () => LogDropped(logger, jobId));
        _subscribers[id] = subscriber;
        try
        {
            await foreach (var evt in subscriber.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
        }
    }

    private sealed class Subscriber(long? jobId, Action onDrop)
    {
        private readonly Channel<AgentEventDto> _channel = Channel.CreateBounded<AgentEventDto>(
            new BoundedChannelOptions(BufferSize) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => onDrop());

        public long? JobId { get; } = jobId;

        public ChannelReader<AgentEventDto> Reader => _channel.Reader;

        public void Write(AgentEventDto evt) => _channel.Writer.TryWrite(evt);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live event subscriber (job {JobId}) fell behind; dropped the oldest buffered event")]
    private static partial void LogDropped(ILogger logger, long? jobId);
}
