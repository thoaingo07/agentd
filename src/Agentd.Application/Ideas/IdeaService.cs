using System.Collections.Concurrent;
using System.Globalization;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace Agentd.Application.Ideas;

/// <summary>
/// Brainstorm threads: <c>!idea</c> opens one with a read-only agent session in a detached checkout of the
/// repository; every message resumes the same session. One turn at a time per idea (messages that arrive
/// meanwhile are answered together next), and at most <see cref="MaxConcurrentIdeas"/> ideas think at once.
/// Replies go straight to the thread (not the job outbox: an idea has no job).
/// </summary>
public sealed partial class IdeaService(
    IIdeaStore ideas,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    IMessagingProviderRegistry providers,
    ILogger<IdeaService> logger) : IDisposable
{
    public const int MaxConcurrentIdeas = 2;
    public const int TitleLength = 60;

    private readonly ConcurrentDictionary<long, Queue> _queues = new();
    private readonly SemaphoreSlim _slots = new(MaxConcurrentIdeas, MaxConcurrentIdeas);

    public void Dispose() => _slots.Dispose();

    /// <summary>Starts an idea from <c>!idea [--repo r] [--model m] [--effort e] &lt;text&gt;</c>; returns the reply for the channel.</summary>
    public async Task<Result<string>> StartAsync(ProviderKey provider, string author, string text, string? repository, CancellationToken ct, string? model = null, string? effort = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DomainError.Validation("Describe the idea after `!idea`, e.g. `!idea dark mode for the portal`.");
        }

        var repo = await ResolveRepositoryAsync(repository, ct).ConfigureAwait(false);
        if (!repo.IsSuccess)
        {
            return repo.Error;
        }

        var title = Title(text);
        var opening = new OutboundMessage(MessageKind.Info,
            $"💡 **Idea from {author}** (`{repo.Value.Name}`)\n\n{text.Trim()}\n\nI'm reading the code and will reply here. Talk to me in this thread; when it's clear, I'll propose User Stories and Tasks.");
        var thread = await providers.Resolve(provider).OpenConversationAsync(
            new ConversationSpec(default, default, title, repo.Value.Name, opening, $"💡 Idea: {title}"), ct).ConfigureAwait(false);
        var id = await ideas.InsertAsync(repo.Value.Name.Value, title, author, provider, thread.ExternalConversationId, thread.ExternalSpaceId, ct).ConfigureAwait(false);
        if ((model ?? effort) is not null && await ideas.GetAsync(id, ct).ConfigureAwait(false) is { } created)
        {
            await ideas.SaveAsync(created with { Model = model, Effort = effort }, ct).ConfigureAwait(false);
        }

        await ideas.AddMessageAsync(id, "in", author, text.Trim(), ct).ConfigureAwait(false);

        var prompt = string.Create(CultureInfo.InvariantCulture,
            $"Brainstorm this idea with the developer, {author}:\n\n{text.Trim()}\n\nRepository: {repo.Value.Name} (a read-only checkout of `{repo.Value.BaseBranch}` is your working directory).");
        Enqueue(id, prompt);
        var settings = (model, effort) switch
        {
            (null, null) => string.Empty,
            _ => $" Model: {model ?? "default"}, effort: {effort ?? "default"}.",
        };
        return $"💡 Started a brainstorm thread for your idea (idea #{id}).{settings}";
    }

    /// <summary><c>!model &lt;name&gt;</c> / <c>!effort &lt;level&gt;</c> in an idea's thread: used from the next reply.</summary>
    public async Task<string> ChangeSettingsAsync(Idea idea, string? model, string? effort, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(idea);
        if (model is not null && !BrainstormSettings.IsModel(model))
        {
            return $"`{model}` isn't a model name (use fable, opus, sonnet or a full model name).";
        }

        if (effort is not null && !BrainstormSettings.IsEffort(effort))
        {
            return $"`{effort}` isn't an effort level ({string.Join(", ", BrainstormSettings.Efforts)}).";
        }

        var updated = idea with { Model = model ?? idea.Model, Effort = effort?.ToLowerInvariant() ?? idea.Effort };
        await ideas.SaveAsync(updated, ct).ConfigureAwait(false);
        return $"⚙️ From my next reply: model **{updated.Model ?? "default"}**, effort **{updated.Effort ?? "default"}**.";
    }

    /// <summary>A message in an idea's thread. Returns false when the idea no longer takes messages.</summary>
    public async Task<bool> HandleMessageAsync(Idea idea, string author, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(idea);
        if (idea.Status is IdeaStatus.Closed or IdeaStatus.Discarded or IdeaStatus.Created)
        {
            await PostAsync(idea, new OutboundMessage(MessageKind.Info, "This idea is finished, so I didn't pass your message on. Start a new one with `!idea <text>`."), ct).ConfigureAwait(false);
            return false;
        }

        await ideas.AddMessageAsync(idea.Id, "in", author, text, ct).ConfigureAwait(false);
        if (!Enqueue(idea.Id, $"{author}: {text}"))
        {
            await PostAsync(idea, new OutboundMessage(MessageKind.Info, "💭 Still thinking about the last message; I'll answer this right after."), ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Queues a prompt; returns false if a turn is already running (the prompt joins the next turn).</summary>
    private bool Enqueue(long ideaId, string prompt)
    {
        var queue = _queues.GetOrAdd(ideaId, _ => new Queue());
        lock (queue)
        {
            queue.Prompts.Add(prompt);
            if (queue.Running)
            {
                return false;
            }

            queue.Running = true;
        }

        _ = Task.Run(() => DrainAsync(ideaId, queue));
        return true;
    }

    private async Task DrainAsync(long ideaId, Queue queue)
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
                await TurnAsync(ideaId, prompt, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // one failed turn must not stop the idea's queue
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogTurnFailed(logger, ex, ideaId);
            }
            finally
            {
                _slots.Release();
            }
        }
    }

    private async Task TurnAsync(long ideaId, string prompt, CancellationToken ct)
    {
        if (await ideas.GetAsync(ideaId, ct).ConfigureAwait(false) is not { } idea)
        {
            return;
        }

        var worktree = idea.Worktree;
        if (worktree is null || !Directory.Exists(worktree))
        {
            var repo = await repositories.GetAsync(RepositoryName.From(idea.Repository), ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Repository '{idea.Repository}' is no longer registered.");
            worktree = await worktrees.CheckoutDetachedAsync(repo, $"idea-{idea.Id}", ct).ConfigureAwait(false);
        }

        var session = idea.Session ?? Guid.NewGuid();
        var reply = await agent.RunAsync(new BrainstormTurn(idea.Id, worktree, session, idea.Session is not null, prompt, idea.Model, idea.Effort), ct).ConfigureAwait(false);
        idea = idea with { Session = session, Worktree = worktree };

        if (reply.UsageLimitedUntil is { } until || reply.Text is null)
        {
            var why = reply.UsageLimitedUntil is { } u
                ? $"⏸ The Claude usage limit is reached until {u:HH:mm} UTC. Send your message again after that."
                : $"⚠️ I couldn't finish that reply ({reply.Error ?? "no answer"}). Send it again to retry.";
            await ideas.SaveAsync(idea, ct).ConfigureAwait(false);
            await PostAsync(idea, new OutboundMessage(MessageKind.Info, why), ct).ConfigureAwait(false);
            return;
        }

        var (text, drafts, problem) = WorkItemDrafts.Extract(reply.Text);
        if (drafts is not null)
        {
            idea = idea with { Drafts = drafts, Status = IdeaStatus.Proposed };
        }

        await ideas.SaveAsync(idea, ct).ConfigureAwait(false);
        await ideas.AddMessageAsync(idea.Id, "out", "agent", reply.Text, ct).ConfigureAwait(false);
        if (text.Length > 0)
        {
            await PostAsync(idea, new OutboundMessage(MessageKind.Info, text), ct).ConfigureAwait(false);
        }

        if (drafts is not null)
        {
            await PostAsync(idea, new OutboundMessage(MessageKind.Question,
                WorkItemDrafts.Render(drafts) + "\n\nReply with changes and I'll revise them. Creating them in Azure DevOps comes in the next update."), ct).ConfigureAwait(false);
        }
        else if (problem is not null)
        {
            Enqueue(idea.Id, $"(agentd) Your work-items block couldn't be read: {problem}. Send the corrected block.");
        }
    }

    private async Task<Result<Domain.Repositories.Repository>> ResolveRepositoryAsync(string? name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            return RepositoryName.Create(name) is { IsSuccess: true } valid && await repositories.GetAsync(valid.Value, ct).ConfigureAwait(false) is { } named
                ? named
                : DomainError.NotFound($"Repository '{name}'");
        }

        var all = await repositories.ListAsync(ct).ConfigureAwait(false);
        return all.Count switch
        {
            1 => all[0],
            0 => DomainError.Validation("No repository is registered yet: an admin can add one with `agentd repo add <url>`."),
            _ => DomainError.Validation($"Several repositories are registered: add `--repo <name>` ({string.Join(", ", all.Select(r => r.Name.Value))})."),
        };
    }

    private async Task PostAsync(Idea idea, OutboundMessage message, CancellationToken ct)
    {
        try
        {
            var provider = providers.Resolve(idea.Provider);
            foreach (var part in MessageChunker.Prepare(message, provider.Capabilities))
            {
                await provider.SendAsync(new ConversationRef(idea.Provider, idea.ThreadId, idea.SpaceId), part, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostFailed(logger, ex, idea.Id);
        }
    }

    internal static string Title(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= TitleLength ? line : line[..(TitleLength - 1)].TrimEnd() + "…";
    }

    private sealed class Queue
    {
        public List<string> Prompts { get; } = [];

        public bool Running { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Idea {IdeaId}: the brainstorm turn failed")]
    private static partial void LogTurnFailed(ILogger logger, Exception exception, long ideaId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Idea {IdeaId}: posting to the thread failed")]
    private static partial void LogPostFailed(ILogger logger, Exception exception, long ideaId);
}
