using System.Collections.Concurrent;
using System.Globalization;
using Agentd.Application.Ideas;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Chats;

/// <summary>
/// Chat threads: <c>!chat &lt;question&gt;</c> opens one with a read-only agent session over every registered repository
/// (a detached checkout of each base branch; <c>--repo</c> narrows it). Every message in the thread resumes the same
/// session; <c>close</c> ends it. One turn at a time per chat, and at most <see cref="MaxConcurrentChats"/> chats think at once.
/// </summary>
public sealed partial class ChatService(
    IChatStore chats,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    IMessagingProviderRegistry providers,
    ILogger<ChatService> logger,
    IOptions<Jobs.JobOptions>? jobs = null,
    IMcpTokenIssuer? tokens = null) : IDisposable
{
    public const int MaxConcurrentChats = 2;

    private readonly ConcurrentDictionary<long, Queue> _queues = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentChats, MaxConcurrentChats);

    public void Dispose() => _slots.Dispose();

    /// <summary><c>chat &lt;question&gt; [--repo r] [--model m] [--effort e]</c>; returns the reply for the channel.</summary>
    public async Task<Result<string>> StartAsync(ProviderKey provider, string author, IReadOnlyList<string> args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        var (question, repoName, model, effort, problem) = BrainstormSettings.Parse(args);
        if (problem is not null)
        {
            return DomainError.Validation(problem);
        }

        if (string.IsNullOrWhiteSpace(question))
        {
            return DomainError.Validation("Ask something after `!chat`, e.g. `!chat where do we validate the login token?`.");
        }

        var all = await repositories.ListAsync(ct).ConfigureAwait(false);
        var repos = repoName is null ? all : [.. all.Where(r => string.Equals(r.Name.Value, repoName, StringComparison.OrdinalIgnoreCase))];
        if (repos.Count == 0)
        {
            return DomainError.Validation(repoName is null ? "No repositories are registered yet: `!repo add <url>`." : $"No repository `{repoName}`.");
        }

        var defaults = jobs?.Value.Steps.TryGetValue(Jobs.JobSteps.Chat, out var step) == true ? step : null;
        model ??= string.IsNullOrWhiteSpace(defaults?.Model) ? null : defaults.Model.Trim();
        effort ??= string.IsNullOrWhiteSpace(defaults?.Effort) ? null : defaults.Effort.Trim().ToLowerInvariant();
        var title = IdeaService.Title(question);
        var opening = new OutboundMessage(MessageKind.Info,
            $"💬 **{author} asks:** {question.Trim()}\n\nI'm reading the code of {string.Join(", ", repos.Select(r => $"`{r.Name}`"))} (read-only). " +
            "Ask follow-ups in this thread; say **close** when you're done.\n" + Jobs.StepAnnouncer.ModelNote(model, effort));
        var thread = await providers.Resolve(provider).OpenConversationAsync(
            new ConversationSpec(default, default, title, repos[0].Name, opening, $"💬 Chat: {title}"), ct).ConfigureAwait(false);
        var id = await chats.InsertAsync(author, provider, thread.ExternalConversationId, thread.ExternalSpaceId, [.. repos.Select(r => r.Name.Value)], ct).ConfigureAwait(false);
        if ((model ?? effort) is not null && await chats.GetAsync(id, ct).ConfigureAwait(false) is { } created)
        {
            await chats.SaveAsync(created with { Model = model, Effort = effort }, ct).ConfigureAwait(false);
        }

        await chats.AddMessageAsync(id, "in", author, question.Trim(), ct).ConfigureAwait(false);
        Enqueue(id, $"{author} asks:\n\n{question.Trim()}");
        return $"💬 Opened a chat thread for your question (chat #{id}).";
    }

    /// <summary>A message in a chat's thread. Returns false when the chat no longer takes messages.</summary>
    public async Task<bool> HandleMessageAsync(Chat chat, string author, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(text);
        if (chat.Status == ChatStatus.Closed)
        {
            return false;
        }

        if (text.Trim().TrimEnd('.', '!').ToLowerInvariant() is "close" or "done" or "end")
        {
            await CloseAsync(chat, author, ct).ConfigureAwait(false);
            return true;
        }

        await chats.AddMessageAsync(chat.Id, "in", author, text, ct).ConfigureAwait(false);
        if (!Enqueue(chat.Id, $"{author}: {text}"))
        {
            await PostAsync(chat, new OutboundMessage(MessageKind.Info, "💭 Still answering the last message; I'll take this one right after."), ct).ConfigureAwait(false);
        }

        return true;
    }

    private async Task CloseAsync(Chat chat, string author, CancellationToken ct)
    {
        tokens?.RevokeChat(chat.Id);
        await chats.SaveAsync(chat with { Status = ChatStatus.Closed, Worktrees = [] }, ct).ConfigureAwait(false);
        await PostAsync(chat, new OutboundMessage(MessageKind.Info, $"🧹 Chat closed by {author}. The conversation is kept; `!chat <question>` starts a new one."), ct).ConfigureAwait(false);
        foreach (var (repo, worktree) in chat.Repositories.Zip(chat.Worktrees))
        {
            try
            {
                if (await repositories.GetAsync(RepositoryName.From(repo), ct).ConfigureAwait(false) is { } registered)
                {
                    await worktrees.RemoveAsync(registered, new WorktreePath(worktree), ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogRemoveFailed(logger, ex, worktree);
            }
        }
    }

    /// <summary>Queues a prompt; returns false if a turn is already running (the prompt joins the next turn).</summary>
    private bool Enqueue(long chatId, string prompt)
    {
        var queue = _queues.GetOrAdd(chatId, _ => new Queue());
        lock (queue)
        {
            queue.Prompts.Add(prompt);
            if (queue.Running)
            {
                return false;
            }

            queue.Running = true;
        }

        _ = Task.Run(() => DrainAsync(chatId, queue));
        return true;
    }

    private async Task DrainAsync(long chatId, Queue queue)
    {
        while (true)
        {
            string prompt;
            lock (queue)
            {
                if (queue.Prompts.Count == 0)
                {
                    queue.Running = false;
                    return;
                }

                prompt = string.Join("\n\n", queue.Prompts);
                queue.Prompts.Clear();
            }

            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                await TurnAsync(chatId, prompt, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // one failed turn must not stop the chat's queue
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogTurnFailed(logger, ex, chatId);
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    private async Task TurnAsync(long chatId, string prompt, CancellationToken ct)
    {
        if (await chats.GetAsync(chatId, ct).ConfigureAwait(false) is not { Status: ChatStatus.Open } chat)
        {
            return;
        }

        // A read-only checkout of each repository's base branch: the first is the working directory, the rest are added.
        var paths = new List<string>();
        var where = new List<string>();
        foreach (var name in chat.Repositories)
        {
            if (await repositories.GetAsync(RepositoryName.From(name), ct).ConfigureAwait(false) is { } repo)
            {
                var path = await worktrees.CheckoutDetachedAsync(repo, $"chat-{chat.Id}", ct).ConfigureAwait(false);
                paths.Add(path);
                where.Add($"- {repo.Name} ({repo.BaseBranch}): {path}");
            }
        }

        if (paths.Count == 0)
        {
            await PostAsync(chat, new OutboundMessage(MessageKind.Info, "⚠️ None of this chat's repositories is registered anymore."), ct).ConfigureAwait(false);
            return;
        }

        if (chat.Session is null)
        {
            prompt = string.Create(CultureInfo.InvariantCulture, $"The repositories (read-only checkouts):\n{string.Join('\n', where)}\n\n{prompt}");
        }

        var session = chat.Session ?? Guid.NewGuid();
        // The chat's token lets the agent call agentd's read-only Azure DevOps tools (agentd answers with its own credentials).
        var reply = await agent.RunAsync(new BrainstormTurn(chat.Id, paths[0], session, chat.Session is not null, prompt, chat.Model, chat.Effort, ThreadTurnKind.Chat,
            [.. paths.Skip(1)], tokens?.IssueChat(chat.Id)), ct).ConfigureAwait(false);
        chat = chat with { Session = session, Worktrees = paths };
        await chats.SaveAsync(chat, ct).ConfigureAwait(false);
        if (reply.UsageLimitedUntil is { } until || reply.Text is null)
        {
            var why = reply.UsageLimitedUntil is { } u
                ? $"⏸ The Claude usage limit is reached until {u:HH:mm} UTC. Ask again after that."
                : $"⚠️ I couldn't finish that answer ({reply.Error ?? "no answer"}). Ask again to retry.";
            await PostAsync(chat, new OutboundMessage(MessageKind.Info, why), ct).ConfigureAwait(false);
            return;
        }

        await chats.AddMessageAsync(chat.Id, "out", "agent", reply.Text, ct).ConfigureAwait(false);
        await PostAsync(chat, new OutboundMessage(MessageKind.Info, reply.Text.Trim()), ct).ConfigureAwait(false);
    }

    private async Task PostAsync(Chat chat, OutboundMessage message, CancellationToken ct)
    {
        try
        {
            var provider = providers.Resolve(chat.Provider);
            foreach (var part in MessageChunker.Prepare(message, provider.Capabilities))
            {
                await provider.SendAsync(new ConversationRef(chat.Provider, chat.ThreadId, chat.SpaceId), part, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostFailed(logger, ex, chat.Id);
        }
    }

    private sealed class Queue
    {
        public List<string> Prompts { get; } = [];

        public bool Running { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Chat {ChatId}: the turn failed")]
    private static partial void LogTurnFailed(ILogger logger, Exception exception, long chatId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Chat {ChatId}: posting to the thread failed")]
    private static partial void LogPostFailed(ILogger logger, Exception exception, long chatId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the checkout {Worktree} failed; the sweep retries")]
    private static partial void LogRemoveFailed(ILogger logger, Exception exception, string worktree);
}
