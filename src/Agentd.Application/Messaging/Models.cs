using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Messaging;

/// <summary>Platform limits and features.</summary>
/// <param name="MaxMessageLength">Longest message the platform accepts (Discord: 2000).</param>
/// <param name="SupportsConversations">Threads or topics; false means the reply-chain fallback.</param>
/// <param name="SupportsOptions">Buttons for <c>ask_developer(options)</c>; otherwise a numbered list.</param>
/// <param name="SupportsAttachments">Files (logs, diffs); otherwise a link to the Web UI.</param>
/// <param name="SupportsEditing">Edit one live status message instead of posting many.</param>
public sealed record MessagingCapabilities(
    int MaxMessageLength,
    bool SupportsConversations,
    bool SupportsOptions,
    bool SupportsAttachments,
    bool SupportsEditing);

/// <summary>Why a message is sent; providers may style it (e.g. an error colour).</summary>
public enum MessageKind
{
    /// <summary>General information.</summary>
    Info,

    /// <summary>A progress update (coalesced or edited in place).</summary>
    Progress,

    /// <summary>A question from the agent; the job waits for the reply.</summary>
    Question,

    /// <summary>The outcome of a job (e.g. the pull request).</summary>
    Result,

    /// <summary>A failure.</summary>
    Error,
}

/// <summary>
/// A provider-neutral message. <see cref="Markdown"/> is a CommonMark subset: paragraphs,
/// <c>**bold**</c>, <c>*italic*</c>, inline code, fenced code blocks and links; nothing else.
/// </summary>
public sealed record OutboundMessage(
    MessageKind Kind,
    string Markdown,
    IReadOnlyList<MessageOption>? Options = null,
    IReadOnlyList<Attachment>? Attachments = null)
{
    /// <summary>A message needs text or at least one attachment.</summary>
    public static Result<OutboundMessage> Create(MessageKind kind, string? markdown, IReadOnlyList<MessageOption>? options = null, IReadOnlyList<Attachment>? attachments = null) =>
        string.IsNullOrWhiteSpace(markdown) && (attachments is null || attachments.Count == 0)
            ? DomainError.Validation("A message needs text or an attachment.")
            : new OutboundMessage(kind, markdown ?? "", options, attachments);
}

/// <summary>A button. <see cref="Id"/> is 1–32 characters of <c>[A-Za-z0-9_-]</c>, so it fits in callback data once prefixed.</summary>
public sealed record MessageOption(string Id, string Label)
{
    /// <summary>Longest allowed option id.</summary>
    public const int MaxIdLength = 32;

    /// <summary>Validates the id and label.</summary>
    public static Result<MessageOption> Create(string? id, string? label)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxIdLength || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            return DomainError.Validation($"Option id must be 1–{MaxIdLength} characters of A–Z, a–z, 0–9, '_' or '-' (was '{id}').");
        }

        return string.IsNullOrWhiteSpace(label)
            ? DomainError.Validation("Option label is required.")
            : new MessageOption(id, label.Trim());
    }
}

/// <summary>A file sent with a message.</summary>
public sealed record Attachment(string FileName, string ContentType, ReadOnlyMemory<byte> Content);

/// <summary>A message from a developer, already normalized by the provider.</summary>
/// <param name="Provider">The provider it came from.</param>
/// <param name="ExternalMessageId">The platform's message (or interaction) id, used for idempotency.</param>
/// <param name="ExternalConversationId">Thread id (Discord) the message belongs to.</param>
/// <param name="ExternalUserId">The sender's platform user id (mapped through the user directory).</param>
/// <param name="UserDisplayName">The sender's display name, for transcripts.</param>
/// <param name="Text">The message text, when it is a plain message.</param>
/// <param name="Command">A normalized command (slash command), when it is one.</param>
/// <param name="SelectedOptionId">The option id of a pressed button; <paramref name="Text"/> then carries the option's label.</param>
/// <param name="SentAt">When the platform says it was sent.</param>
public sealed record InboundMessage(
    ProviderKey Provider,
    string ExternalMessageId,
    string ExternalConversationId,
    string ExternalUserId,
    string UserDisplayName,
    string? Text,
    InboundCommand? Command,
    string? SelectedOptionId,
    DateTimeOffset SentAt);

/// <summary>A command in neutral form (e.g. <c>status</c>, <c>cancel</c>); unknown names are rejected by the Application layer.</summary>
public sealed record InboundCommand(string Name, IReadOnlyList<string> Args);

/// <summary>What a provider needs to open a conversation (a job's, or an idea's).</summary>
/// <param name="JobId">The job (default for a thread that isn't a job's, e.g. an idea).</param>
/// <param name="WorkItemId">Its work item (default when there is none).</param>
/// <param name="Title">The job or idea title.</param>
/// <param name="Repository">The repository.</param>
/// <param name="Opening">The first message.</param>
/// <param name="Name">The thread name; null = <c>WI-1234 · Title</c>.</param>
public sealed record ConversationSpec(JobId JobId, WorkItemId WorkItemId, string Title, RepositoryName Repository, OutboundMessage Opening, string? Name = null);

/// <summary>A conversation on a provider: the thread id and, where it matters, its space (guild, chat).</summary>
public sealed record ConversationRef(ProviderKey Provider, string ExternalConversationId, string? ExternalSpaceId);

/// <summary>A posted message, for editing.</summary>
public sealed record MessageRef(ConversationRef Conversation, string ExternalMessageId);

/// <summary>Result of a provider health check.</summary>
public sealed record ProviderHealth(bool Healthy, string Detail);
