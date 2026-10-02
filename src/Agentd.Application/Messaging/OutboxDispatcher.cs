using System.Collections.Concurrent;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Messaging;

/// <summary>A claimed outbox row, with what is needed to deliver it.</summary>
public sealed record OutboxItem(
    long Id,
    JobId JobId,
    ConversationRef Conversation,
    OutboundMessage Message,
    bool ReplaceStatusMessage,
    string? StatusMessageId,
    int Attempts);

/// <summary>Claims and settles outbox rows (PostgreSQL routines).</summary>
public interface IOutboxDelivery
{
    /// <summary>Claims due rows for <paramref name="providers"/>: the oldest unsent row per conversation.</summary>
    Task<IReadOnlyList<OutboxItem>> ClaimAsync(int limit, IReadOnlyList<ProviderKey> providers, CancellationToken cancellationToken);

    Task MarkSentAsync(long id, string externalMessageId, bool createdStatusMessage, CancellationToken cancellationToken);

    /// <summary>Schedules a retry; returns false when the row is now dead (too many attempts).</summary>
    Task<bool> MarkRetryAsync(long id, string reason, DateTimeOffset nextAttemptAt, int maxAttempts, CancellationToken cancellationToken);

    Task MarkFailedAsync(long id, string reason, CancellationToken cancellationToken);

    /// <summary>Returns rows claimed before <paramref name="claimedBefore"/> and never settled to pending.</summary>
    Task<int> ReleaseStaleAsync(DateTimeOffset claimedBefore, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by providers to classify a failed delivery. Anything else counts as transient.
/// <see cref="RetryAfter"/> (a 429) overrides the back-off.
/// </summary>
public sealed class MessagingDeliveryException(string message, bool permanent, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public bool Permanent { get; } = permanent;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Delivers outbox rows: fits each message to the provider (<see cref="MessageChunker"/>), edits the
/// live status message for progress where supported, retries transient failures with back-off, and
/// pauses a provider whose sends keep failing (circuit breaker). A provider outage never touches jobs:
/// rows wait and flush later, in order per conversation.
/// </summary>
public sealed class OutboxDispatcher(IOutboxDelivery delivery, IMessagingProviderRegistry providers, IClock clock)
{
    public const int MaxAttempts = 20;
    public const int CircuitThreshold = 5;
    public static readonly TimeSpan CircuitOpenFor = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<ProviderKey, Circuit> _circuits = new();

    /// <summary>Providers whose circuit is open (sends paused).</summary>
    public IReadOnlyList<ProviderKey> PausedProviders =>
        _circuits.Where(c => c.Value.OpenUntil > clock.UtcNow).Select(c => c.Key).ToList();

    /// <summary>Claims and delivers one batch; returns the number of rows claimed.</summary>
    public async Task<int> DispatchOnceAsync(int batchSize, CancellationToken cancellationToken)
    {
        var paused = PausedProviders;
        var active = providers.Enabled.Select(p => p.Key).Where(k => !paused.Contains(k)).ToList();
        if (active.Count == 0)
        {
            return 0;
        }

        var items = await delivery.ClaimAsync(batchSize, active, cancellationToken).ConfigureAwait(false);
        // Claimed rows belong to different conversations, so they can go out in parallel.
        await Task.WhenAll(items.Select(item => DeliverAsync(item, cancellationToken))).ConfigureAwait(false);
        return items.Count;
    }

    private async Task DeliverAsync(OutboxItem item, CancellationToken ct)
    {
        var key = item.Conversation.Provider;
        try
        {
            var provider = providers.Resolve(key);
            var (externalId, createdStatus) = await SendAsync(provider, item, ct).ConfigureAwait(false);
            await delivery.MarkSentAsync(item.Id, externalId, createdStatus, CancellationToken.None).ConfigureAwait(false);
            _circuits.AddOrUpdate(key, _ => new Circuit(0, DateTimeOffset.MinValue), (_, c) => c with { Failures = 0 });
        }
        catch (MessagingDeliveryException ex) when (ex.Permanent)
        {
            await delivery.MarkFailedAsync(item.Id, ex.Message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var delay = (ex as MessagingDeliveryException)?.RetryAfter ?? Backoff(item.Attempts);
            await delivery.MarkRetryAsync(item.Id, ex.Message, clock.UtcNow + delay, MaxAttempts, CancellationToken.None).ConfigureAwait(false);
            var circuit = _circuits.AddOrUpdate(key, _ => new Circuit(1, DateTimeOffset.MinValue), (_, c) => c with { Failures = c.Failures + 1 });
            if (circuit.Failures >= CircuitThreshold)
            {
                _circuits[key] = new Circuit(0, clock.UtcNow + CircuitOpenFor);
            }
        }
    }

    private static async Task<(string ExternalId, bool CreatedStatus)> SendAsync(IMessagingProvider provider, OutboxItem item, CancellationToken ct)
    {
        if (item.ReplaceStatusMessage && provider.Capabilities.SupportsEditing)
        {
            var fitted = MessageChunker.Prepare(item.Message, provider.Capabilities)[^1];
            if (item.StatusMessageId is { } statusId)
            {
                await provider.EditAsync(new MessageRef(item.Conversation, statusId), fitted, ct).ConfigureAwait(false);
                return (statusId, false);
            }

            var created = await provider.SendAsync(item.Conversation, fitted, ct).ConfigureAwait(false);
            return (created.ExternalMessageId, true);
        }

        string? first = null;
        foreach (var part in MessageChunker.Prepare(item.Message, provider.Capabilities))
        {
            var sent = await provider.SendAsync(item.Conversation, part, ct).ConfigureAwait(false);
            first ??= sent.ExternalMessageId;
        }

        return (first!, false);
    }

    /// <summary>min(2^attempts s, 5 min) plus up to 1 s of jitter.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Min(attempts, 16)), MaxBackoff.TotalSeconds))
        + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 1000));

    private sealed record Circuit(int Failures, DateTimeOffset OpenUntil);
}
