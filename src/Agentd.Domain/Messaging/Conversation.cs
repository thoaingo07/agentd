using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Domain.Messaging;

/// <summary>Database identity of a conversation.</summary>
public readonly record struct ConversationId(long Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// A job's thread on one messaging provider (a Discord thread, a Telegram forum topic). A job has at
/// most one open conversation per provider; storage enforces it with a unique index as well.
/// </summary>
public sealed class Conversation : AggregateRoot<ConversationId>
{
    private Conversation()
    {
    }

    public JobId JobId { get; private set; }

    public ProviderKey Provider { get; private set; }

    /// <summary>Discord thread id, or <c>chatId:topicId</c> on Telegram.</summary>
    public string ExternalConversationId { get; private set; } = "";

    /// <summary>Discord guild/channel, or the Telegram chat id.</summary>
    public string? ExternalSpaceId { get; private set; }

    public Uri? Link { get; private set; }

    /// <summary>The message edited in place by progress updates.</summary>
    public string? StatusMessageId { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public bool IsOpen => ClosedAt is null;

    /// <summary>
    /// Opens a conversation for <paramref name="job"/> on <paramref name="provider"/>; rejected when
    /// one of <paramref name="existing"/> (the job's conversations) is still open on that provider.
    /// </summary>
    public static Result<Conversation> Open(
        JobId job,
        ProviderKey provider,
        string externalConversationId,
        string? externalSpaceId,
        Uri? link,
        IEnumerable<Conversation> existing,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (string.IsNullOrWhiteSpace(externalConversationId))
        {
            return DomainError.Validation("External conversation id is required.");
        }

        if (existing.Any(c => c.JobId == job && c.Provider == provider && c.IsOpen))
        {
            return DomainError.Conflict($"Job {job} already has an open conversation on {provider}.");
        }

        var conversation = new Conversation
        {
            JobId = job,
            Provider = provider,
            ExternalConversationId = externalConversationId,
            ExternalSpaceId = externalSpaceId,
            Link = link,
            OpenedAt = now,
        };
        conversation.Raise(new ConversationOpened(job, provider, externalConversationId, link, now));
        return conversation;
    }

    public static Conversation Rehydrate(ConversationSnapshot s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new Conversation
        {
            Id = s.Id,
            JobId = s.JobId,
            Provider = s.Provider,
            ExternalConversationId = s.ExternalConversationId,
            ExternalSpaceId = s.ExternalSpaceId,
            Link = s.Link,
            StatusMessageId = s.StatusMessageId,
            OpenedAt = s.OpenedAt,
            ClosedAt = s.ClosedAt,
        };
    }

    /// <summary>Called by storage after an insert.</summary>
    public void Persisted(ConversationId id) => Id = id;

    public void SetStatusMessage(string externalMessageId) => StatusMessageId = externalMessageId;

    public void ClearStatusMessage() => StatusMessageId = null;

    /// <summary>Called by storage after the conversation was handed to a new job of the same work item.</summary>
    public void MovedTo(JobId jobId) => JobId = jobId;

    public Result Close(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return DomainError.InvalidTransition("Closed", "close");
        }

        ClosedAt = now;
        return Result.Ok;
    }
}

/// <summary>Stored state of a conversation.</summary>
public sealed record ConversationSnapshot(
    ConversationId Id,
    JobId JobId,
    ProviderKey Provider,
    string ExternalConversationId,
    string? ExternalSpaceId,
    Uri? Link,
    string? StatusMessageId,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt);
