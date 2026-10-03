using System.Globalization;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.Events;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Messaging;

/// <summary>How a message fans out over a job's open conversations.</summary>
/// <param name="OnlyProviders">Send only to these providers (null = all).</param>
/// <param name="ExceptProviders">Skip these providers (mirroring a reply back to the others).</param>
/// <param name="ReplaceStatusMessage">Progress: edit the conversation's status message, or keep only the newest pending update.</param>
public sealed record EnqueueOptions(
    IReadOnlyList<ProviderKey>? OnlyProviders = null,
    IReadOnlyList<ProviderKey>? ExceptProviders = null,
    bool ReplaceStatusMessage = false);

/// <summary>A message for a job's conversations, written to the outbox and delivered by the dispatcher.</summary>
public sealed record OutboxMessage(OutboundMessage Message, EnqueueOptions? Options = null);

/// <summary>
/// The outbox for messages that don't come with a job state change (progress, mirrored replies).
/// Messages caused by a state change are written by the job repository in the same transaction.
/// </summary>
public interface IOutbox
{
    /// <summary>Writes one row per matching open conversation of the job.</summary>
    Task EnqueueAsync(JobId jobId, IReadOnlyList<OutboxMessage> messages, CancellationToken cancellationToken);
}

/// <summary>The message (if any) a job's domain event posts to its conversations.</summary>
public static class JobEventMessages
{
    public static IReadOnlyList<OutboxMessage> For(IEnumerable<IDomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        return events.SelectMany(e => e is DeveloperReplied reply ? ForReply(reply) : For(e) is { } message ? [message] : []).ToList();
    }

    /// <summary>
    /// A chat reply is mirrored to the job's other conversations, and a reply queued while the agent
    /// is running is acknowledged where it was written.
    /// </summary>
    private static IEnumerable<OutboxMessage> ForReply(DeveloperReplied e)
    {
        if (e.Via is not { } via)
        {
            yield break;
        }

        yield return new(MessageCatalog.Mirrored(e.From, via.Value, e.Reply), new EnqueueOptions(ExceptProviders: [via]));
        if (!e.Resumed)
        {
            yield return new(new OutboundMessage(MessageKind.Info, "Queued for the agent's next turn."), new EnqueueOptions(OnlyProviders: [via]));
        }
    }

    public static OutboxMessage? For(IDomainEvent domainEvent) => domainEvent switch
    {
        JobFinished e => new(new OutboundMessage(MessageKind.Info, $"Work finished: {e.Summary}\n\nOpening the pull request…")),
        PullRequestCreated e => new(MessageCatalog.PullRequestReady(e.Url.Value, null)),
        PublishRetryScheduled e => new(new OutboundMessage(MessageKind.Info,
            string.Create(CultureInfo.InvariantCulture, $"Publishing failed (attempt {e.Attempt}): {e.Reason}. Retrying at {e.RetryAt:HH:mm} UTC."))),
        JobFailed e => new(MessageCatalog.Failed(e.Reason)),
        JobCancelled e => new(MessageCatalog.Cancelled(e.By)),
        JobDeferred e => new(MessageCatalog.Deferred(e.NotBefore, e.Reason)),
        JobRecovered => new(new OutboundMessage(MessageKind.Info, "agentd restarted; the agent is resuming where it left off.")),
        DeveloperQuestionAsked e => new(MessageCatalog.Question(e.Question, Options(e.Options))),
        WaitReminderSent e => new(new OutboundMessage(MessageKind.Info, string.Create(CultureInfo.InvariantCulture,
            $"**Reminder:** the agent is still waiting for your answer to the question above. Without a reply, the job stops at {e.ExpiresAt:yyyy-MM-dd HH:mm} UTC."))),
        _ => null,
    };

    /// <summary>Option buttons for a question: ids <c>opt1</c>…, labels as given.</summary>
    private static List<MessageOption>? Options(IReadOnlyList<string> labels) =>
        labels.Count == 0
            ? null
            : labels.Select((label, i) => new MessageOption(string.Create(CultureInfo.InvariantCulture, $"opt{i + 1}"), label)).ToList();
}
