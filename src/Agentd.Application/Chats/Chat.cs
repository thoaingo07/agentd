using Agentd.Domain.Messaging;

namespace Agentd.Application.Chats;

public static class ChatStatus
{
    public const string Open = "Open";
    public const string Closed = "Closed";
}

/// <summary>
/// A chat thread (<c>!chat</c>): a read-only agent answering questions about the registered repositories' code, in one
/// session, until someone closes it. <paramref name="Worktrees"/> are its read-only checkouts, one per repository.
/// </summary>
public sealed record Chat(
    long Id,
    string Author,
    ProviderKey Provider,
    string? ThreadId,
    string? SpaceId,
    string Status,
    IReadOnlyList<string> Repositories,
    Guid? Session,
    string? Model,
    string? Effort,
    IReadOnlyList<string> Worktrees);

/// <summary>Chats and their conversation (PostgreSQL routines).</summary>
public interface IChatStore
{
    Task<long> InsertAsync(string author, ProviderKey provider, string? threadId, string? spaceId, IReadOnlyList<string> repositories, CancellationToken cancellationToken);

    Task<Chat?> GetAsync(long id, CancellationToken cancellationToken);

    Task<Chat?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken);

    Task SaveAsync(Chat chat, CancellationToken cancellationToken);

    Task AddMessageAsync(long chatId, string direction, string author, string text, CancellationToken cancellationToken);

    Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(long chatId, CancellationToken cancellationToken);

    /// <summary>The newest chats first, as the Web UI lists them.</summary>
    Task<IReadOnlyList<ChatSummary>> ListAsync(int limit, CancellationToken cancellationToken);

    /// <summary>The open chats' thread ids: the chat poller reads them.</summary>
    Task<IReadOnlyList<string>> ListOpenThreadsAsync(ProviderKey provider, CancellationToken cancellationToken);
}

/// <summary>One message: <c>in</c> from a person, <c>out</c> from the agent (<c>agent</c>) or agentd itself (<c>agentd</c>).</summary>
public sealed record ChatMessage(string Direction, string Author, string Text, DateTimeOffset At);

/// <summary>A chat as the Web UI lists it: its first question is the title; <c>InChat</c>: it has a chat thread.</summary>
public sealed record ChatSummary(long Id, string Author, string Status, IReadOnlyList<string> Repositories, string? Model, string? Effort, bool InChat, string Title,
    int Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
