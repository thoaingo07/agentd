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
/// Replies go straight to the thread (not the job outbox: an idea has no job). An idea started on the Web UI
/// (<see cref="Web"/>) has no thread: its conversation is only in agentd, where agentd's own notices are kept too.
/// </summary>
public sealed partial class IdeaService(
    IIdeaStore ideas,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    IMessagingProviderRegistry providers,
    ILogger<IdeaService> logger,
    IWorkItemSource? workItems = null) : IDisposable
{
    public const int MaxConcurrentIdeas = 2;
    public const int TitleLength = 60;

    /// <summary>The provider of ideas started on the Web UI (no chat thread).</summary>
    public static readonly ProviderKey Web = ProviderKey.From("web");

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

        var started = await StartCoreAsync(provider, author, text, repository, model, effort, ct).ConfigureAwait(false);
        if (!started.IsSuccess)
        {
            return started.Error;
        }

        var settings = (model, effort) switch
        {
            (null, null) => string.Empty,
            _ => $" Model: {model ?? "default"}, effort: {effort ?? "default"}.",
        };
        return $"💡 Started a brainstorm thread for your idea (idea #{started.Value}).{settings}";
    }

    /// <summary>Starts an idea from the Web UI: no chat thread; it's talked through on its page. Returns its id.</summary>
    public async Task<Result<long>> StartOnWebAsync(string author, string text, string? repository, string? model, string? effort, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return DomainError.Validation("Describe the idea.");
        }

        if (model is not null && !BrainstormSettings.IsModel(model))
        {
            return DomainError.Validation($"`{model}` isn't a model name (use fable, opus, sonnet or a full model name).");
        }

        if (effort is not null && !BrainstormSettings.IsEffort(effort))
        {
            return DomainError.Validation($"`{effort}` isn't an effort level ({string.Join(", ", BrainstormSettings.Efforts)}).");
        }

        return await StartCoreAsync(Web, author, text, repository, model, effort?.ToLowerInvariant(), ct).ConfigureAwait(false);
    }

    private async Task<Result<long>> StartCoreAsync(ProviderKey provider, string author, string text, string? repository, string? model, string? effort, CancellationToken ct)
    {
        var repo = await ResolveRepositoryAsync(repository, ct).ConfigureAwait(false);
        if (!repo.IsSuccess)
        {
            return repo.Error;
        }

        var title = Title(text);
        string? threadId = null, spaceId = null;
        if (provider != Web)
        {
            var opening = new OutboundMessage(MessageKind.Info,
                $"💡 **Idea from {author}** (`{repo.Value.Name}`)\n\n{text.Trim()}\n\nI'm reading the code and will reply here. Talk to me in this thread; when it's clear, I'll propose User Stories and Tasks.");
            var thread = await providers.Resolve(provider).OpenConversationAsync(
                new ConversationSpec(default, default, title, repo.Value.Name, opening, $"💡 Idea: {title}"), ct).ConfigureAwait(false);
            (threadId, spaceId) = (thread.ExternalConversationId, thread.ExternalSpaceId);
        }

        var id = await ideas.InsertAsync(repo.Value.Name.Value, title, author, provider, threadId, spaceId, ct).ConfigureAwait(false);
        if ((model ?? effort) is not null && await ideas.GetAsync(id, ct).ConfigureAwait(false) is { } created)
        {
            await ideas.SaveAsync(created with { Model = model, Effort = effort }, ct).ConfigureAwait(false);
        }

        await ideas.AddMessageAsync(id, "in", author, text.Trim(), ct).ConfigureAwait(false);

        var prompt = string.Create(CultureInfo.InvariantCulture,
            $"Brainstorm this idea with the developer, {author}:\n\n{text.Trim()}\n\nRepository: {repo.Value.Name} (a read-only checkout of `{repo.Value.BaseBranch}` is your working directory).");
        Enqueue(id, prompt);
        return id;
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

    public const string LabelCreate = "✅ Create";
    public const string LabelStart = "🚀 Create and start";
    public const string LabelChange = "✏️ Change";
    public const string LabelDiscard = "🗑 Discard";
    public const string LabelDelete = "🗑 Delete thread";
    public const string LabelKeep = "📦 Keep (archive)";

    /// <summary>
    /// A message in an idea's thread, or from its Web UI page (<paramref name="fromWeb"/>: also posted to its chat thread, if
    /// it has one, so the thread keeps the whole conversation). Returns false when the idea no longer takes messages.
    /// </summary>
    public async Task<bool> HandleMessageAsync(Idea idea, string author, string text, CancellationToken ct, bool fromWeb = false)
    {
        ArgumentNullException.ThrowIfNull(idea);
        ArgumentNullException.ThrowIfNull(text);
        if (fromWeb && idea.ThreadId is not null && idea.Status is IdeaStatus.Brainstorming or IdeaStatus.Proposed)
        {
            await PostAsync(idea, new OutboundMessage(MessageKind.Info, $"💬 **{author}** (web): {text.Trim()}"), ct, record: false).ConfigureAwait(false);
        }

        var answer = text.Trim().TrimEnd('.', '!');
        if (idea.Status is IdeaStatus.Created or IdeaStatus.Discarded && CloseOutAnswer(answer) is { } delete)
        {
            await CloseOutAsync(idea, delete, author, ct).ConfigureAwait(false);
            return true;
        }

        if (idea.Status == IdeaStatus.Proposed && Choice(answer) is { } choice)
        {
            await ideas.AddMessageAsync(idea.Id, "in", author, answer, ct).ConfigureAwait(false);
            await ChooseAsync(idea, choice, author, ct).ConfigureAwait(false);
            return true;
        }

        if (idea.Status is IdeaStatus.Closed or IdeaStatus.Discarded or IdeaStatus.Created)
        {
            if (!fromWeb)
            {
                await PostAsync(idea, new OutboundMessage(MessageKind.Info, "This idea is finished, so I didn't pass your message on. Start a new one with `!idea <text>`."), ct, record: false).ConfigureAwait(false);
            }

            return false;
        }

        await ideas.AddMessageAsync(idea.Id, "in", author, text, ct).ConfigureAwait(false);
        if (!Enqueue(idea.Id, $"{author}: {text}"))
        {
            await PostAsync(idea, new OutboundMessage(MessageKind.Info, "💭 Still thinking about the last message; I'll answer this right after."), ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>Whether the agent is working on a reply for idea <paramref name="ideaId"/> (its page says "thinking…").</summary>
    public bool IsThinking(long ideaId) => _queues.TryGetValue(ideaId, out var queue) && queue.Running;

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
            await PostAsync(idea, new OutboundMessage(MessageKind.Info, text), ct, record: false).ConfigureAwait(false);   // stored above, as the agent's
        }

        if (drafts is not null)
        {
            await PostAsync(idea, new OutboundMessage(MessageKind.Question,
                WorkItemDrafts.Render(drafts) + "\n\n**1** create them in Azure DevOps · **2** create and start (agentd picks them up) · **3** change · **4** discard. Or just tell me what to change.",
                [new MessageOption("idea-create", LabelCreate), new MessageOption("idea-start", LabelStart), new MessageOption("idea-change", LabelChange), new MessageOption("idea-discard", LabelDiscard)]), ct, record: false)
                .ConfigureAwait(false);   // the page shows the drafts and the choices itself
        }
        else if (problem is not null)
        {
            Enqueue(idea.Id, $"(agentd) Your work-items block couldn't be read: {problem}. Send the corrected block.");
        }
    }

    private enum IdeaChoice
    {
        Create,
        Start,
        Change,
        Discard,
    }

    private static IdeaChoice? Choice(string answer) =>
        Is(answer, "1", "create", LabelCreate) ? IdeaChoice.Create
        : Is(answer, "2", "start", "create and start", LabelStart) ? IdeaChoice.Start
        : Is(answer, "3", "change", LabelChange) ? IdeaChoice.Change
        : Is(answer, "4", "discard", LabelDiscard) ? IdeaChoice.Discard
        : null;

    /// <summary>true = delete the thread, false = keep (archive), null = not a close-out answer.</summary>
    private static bool? CloseOutAnswer(string answer) =>
        Is(answer, "1", "delete", LabelDelete) ? true : Is(answer, "2", "keep", "archive", LabelKeep) ? false : null;

    private static bool Is(string answer, params string[] options) => options.Any(o => string.Equals(answer, o, StringComparison.OrdinalIgnoreCase));

    private async Task ChooseAsync(Idea idea, IdeaChoice choice, string author, CancellationToken ct)
    {
        switch (choice)
        {
            case IdeaChoice.Change:
                await PostAsync(idea, new OutboundMessage(MessageKind.Info, "✏️ Tell me what to change, and I'll send a revised list."), ct).ConfigureAwait(false);
                return;
            case IdeaChoice.Discard:
                await ideas.SaveAsync(idea with { Status = IdeaStatus.Discarded }, ct).ConfigureAwait(false);
                await PostAsync(idea, new OutboundMessage(MessageKind.Info, $"🗑 Discarded by {author}; nothing was created."), ct).ConfigureAwait(false);
                await AskCloseOutAsync(idea, ct).ConfigureAwait(false);
                return;
            default:
                await CreateAsync(idea, start: choice == IdeaChoice.Start, author, ct).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Creates the drafts in Azure DevOps: stories first, then tasks linked to them; "start" adds the tags agentd polls for.</summary>
    private async Task CreateAsync(Idea idea, bool start, string author, CancellationToken ct)
    {
        if (workItems is null || idea.Drafts is not { Count: > 0 } drafts
            || await repositories.GetAsync(RepositoryName.From(idea.Repository), ct).ConfigureAwait(false) is not { } repo)
        {
            await PostAsync(idea, new OutboundMessage(MessageKind.Info, "I can't create work items here (no drafts, or Azure DevOps isn't configured)."), ct).ConfigureAwait(false);
            return;
        }

        var startTags = start ? new[] { "ai-workflow", repo.MatchTag ?? $"repo:{repo.Name}" } : [];
        var ids = new Dictionary<int, CreatedWorkItem>();
        var order = drafts.Select((d, i) => (d, i)).OrderBy(x => x.d.Parent is null ? 0 : 1).ToList();
        string? failure = null;
        foreach (var (draft, index) in order)
        {
            var tags = (draft.Tags ?? []).Concat(draft.Parent is null ? startTags : []).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var description = $"{draft.Description}\n\nFrom agentd idea #{idea.Id}, brainstormed with {idea.Author}.".Trim();
            try
            {
                ids[index] = await workItems.CreateAsync(new NewWorkItem(draft.Type, draft.Title, description, draft.AcceptanceCriteria, tags, draft.Estimate,
                    draft.Parent is { } p && ids.TryGetValue(p, out var parent) ? parent.Id : null, repo.MatchAreaPaths.Count > 0 ? repo.MatchAreaPaths[0] : null), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = $"\"{draft.Title}\": {ex.Message}";
                break;
            }
        }

        var created = drafts.Select((d, i) => (d, i)).Where(x => ids.ContainsKey(x.i)).ToList();
        await ideas.SaveAsync(idea with { Status = created.Count > 0 ? IdeaStatus.Created : idea.Status, CreatedWorkItems = [.. ids.Values.Select(c => c.Id)] }, ct).ConfigureAwait(false);
        var lines = created.Select(x => $"{(x.d.Parent is null ? "- " : "    - ")}**{x.d.Type} #{ids[x.i].Id}:** {x.d.Title}{(ids[x.i].Url is { } u ? $" ({u})" : string.Empty)}");
        var text = $"✅ **Created in Azure DevOps** by {author}:\n{string.Join('\n', lines)}"
            + (start ? $"\n\n🚀 Tagged `ai-workflow` and `{startTags[1]}`: agentd picks them up at its next poll and opens a thread for each, with the plan for your approval." : string.Empty)
            + (failure is null ? string.Empty : $"\n\n⚠️ Stopped at {failure}. The items above were created; add the rest by hand or start a new idea.");
        await PostAsync(idea, new OutboundMessage(MessageKind.Result, created.Count > 0 ? text : $"⚠️ Nothing was created: {failure}"), ct).ConfigureAwait(false);
        if (created.Count > 0)
        {
            await AskCloseOutAsync(idea, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Asks whether to delete the chat thread; an idea without one only gives back its checkout.</summary>
    private Task AskCloseOutAsync(Idea idea, CancellationToken ct) => idea.ThreadId is null
        ? RemoveCheckoutAsync(idea.Repository, idea.Worktree, ct)
        : PostAsync(idea, new OutboundMessage(MessageKind.Question, "🧹 **All done.** Delete this thread? **1** delete · **2** keep it (archived). The conversation stays in agentd either way.",
            [new MessageOption("idea-delete", LabelDelete), new MessageOption("idea-keep", LabelKeep)]), ct, record: false);

    private async Task CloseOutAsync(Idea idea, bool delete, string author, CancellationToken ct)
    {
        await ideas.SaveAsync(idea with { Status = IdeaStatus.Closed }, ct).ConfigureAwait(false);
        await RemoveCheckoutAsync(idea.Repository, idea.Worktree, ct).ConfigureAwait(false);
        if (idea.ThreadId is not { } threadId)
        {
            return;
        }

        var provider = providers.Resolve(idea.Provider);
        var thread = new ConversationRef(idea.Provider, threadId, idea.SpaceId);
        try
        {
            if (delete)
            {
                await provider.DeleteConversationAsync(thread, ct).ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostFailed(logger, ex, idea.Id);
            await PostAsync(idea, new OutboundMessage(MessageKind.Info, "I couldn't delete this thread (the bot may lack the Manage Threads permission), so I archived it."), ct).ConfigureAwait(false);
        }

        try
        {
            await provider.CloseConversationAsync(thread, $"Closed by {author}", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogPostFailed(logger, ex, idea.Id);
        }
    }

    /// <summary>The read-only checkout isn't needed once the thread is closed (the conversation stays in the database).</summary>
    private async Task RemoveCheckoutAsync(string repository, string? worktree, CancellationToken ct)
    {
        if (worktree is null || await repositories.GetAsync(RepositoryName.From(repository), ct).ConfigureAwait(false) is not { } repo)
        {
            return;
        }

        try
        {
            await worktrees.RemoveAsync(repo, new WorktreePath(worktree), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRemoveFailed(logger, ex, worktree);   // the hourly sweep retries
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

    /// <summary>
    /// agentd's own message about the idea: kept in its conversation (<paramref name="record"/>; the Web UI shows it) and
    /// posted to its chat thread, if it has one.
    /// </summary>
    private async Task PostAsync(Idea idea, OutboundMessage message, CancellationToken ct, bool record = true)
    {
        try
        {
            if (record)
            {
                await ideas.AddMessageAsync(idea.Id, "out", "agentd", message.Markdown, ct).ConfigureAwait(false);
            }

            if (idea.ThreadId is not { } threadId)
            {
                return;
            }

            var provider = providers.Resolve(idea.Provider);
            foreach (var part in MessageChunker.Prepare(message, provider.Capabilities))
            {
                await provider.SendAsync(new ConversationRef(idea.Provider, threadId, idea.SpaceId), part, ct).ConfigureAwait(false);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Removing the checkout {Worktree} failed; the sweep retries")]
    private static partial void LogRemoveFailed(ILogger logger, Exception exception, string worktree);
}
