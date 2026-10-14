using Agentd.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace Agentd.Application.Inbox;

/// <summary>What an Inbox thread is about (docs/architect/inbox.md).</summary>
public static class InboxSources
{
    public const string Job = "job";
    public const string Idea = "idea";
    public const string Review = "review";
    public const string Chat = "chat";

    /// <summary>A watched PR's thread, from before the PR Monitor was turned off: readable, takes no answers.</summary>
    public const string Watch = "watch";
}

/// <summary>One of agentd's messages to a person, for the Inbox; <paramref name="Title"/> names its thread.</summary>
public sealed record InboxEntry(string Source, long SourceId, string Title, string? Recipient, OutboundMessage Message);

/// <summary>A message in a thread.</summary>
public sealed record InboxItem(long Id, string Source, long SourceId, string Title, string? Recipient, string Kind, string Markdown,
    IReadOnlyList<MessageOption> Options, DateTimeOffset CreatedAt);

/// <summary>
/// A thread in a person's list: its latest message, how many they haven't read, and whether its source waits on a person
/// now (then <see cref="Question"/> is the latest question, with its buttons).
/// </summary>
public sealed record InboxThread(string Source, long SourceId, string Title, string? Recipient, long LatestId, string LatestKind, string LatestPreview,
    DateTimeOffset LatestAt, int Items, int Unread, bool Waiting, InboxItem? Question);

/// <summary>The nav badge: threads waiting on the person, and threads with messages they haven't read.</summary>
public sealed record InboxCounts(int Waiting, int Unread);

/// <summary>The Inbox store (<c>agentd.inbox_*</c> routines). Job messages are written by <c>outbox_enqueue</c> itself.</summary>
public interface IInbox
{
    Task<long> AddAsync(InboxEntry entry, CancellationToken cancellationToken);

    /// <summary>The threads for <paramref name="user"/>: waiting on them first, then unread, then the rest; <paramref name="recipient"/> keeps theirs only.</summary>
    Task<IReadOnlyList<InboxThread>> ThreadsAsync(string user, string? recipient, int limit, CancellationToken cancellationToken);

    /// <summary>One thread's messages, newest first, before item <paramref name="before"/>.</summary>
    Task<IReadOnlyList<InboxItem>> ThreadAsync(string source, long sourceId, long? before, int limit, CancellationToken cancellationToken);

    Task MarkReadAsync(string user, string source, long sourceId, long until, CancellationToken cancellationToken);

    Task<InboxCounts> CountsAsync(string user, CancellationToken cancellationToken);

    Task<int> DeleteBeforeAsync(DateTimeOffset before, CancellationToken cancellationToken);
}

/// <summary>Configuration section <c>Agentd:Inbox</c>.</summary>
public sealed class InboxOptions
{
    public const string Section = "Agentd:Inbox";

    /// <summary>Days messages are kept (the hourly sweep deletes older ones); 0 keeps them forever.</summary>
    public int RetentionDays { get; set; } = 90;
}

/// <summary>Writes to the Inbox for the services that post by themselves (ideas, reviews, chats, watches); never fails a post.</summary>
public static partial class InboxWriter
{
    public static async Task TryAddAsync(this IInbox? inbox, InboxEntry entry, ILogger logger, CancellationToken cancellationToken)
    {
        if (inbox is null || entry.Message.Kind == MessageKind.Progress)
        {
            return;
        }

        try
        {
            await inbox.AddAsync(entry, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the Inbox is a second copy: a failed write never stops the message going out
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(logger, ex, entry.Source, entry.SourceId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Writing to the Inbox failed for {Source} {SourceId}; the message still went out")]
    private static partial void LogFailed(ILogger logger, Exception exception, string source, long sourceId);
}
