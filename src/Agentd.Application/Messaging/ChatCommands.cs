using System.Globalization;
using System.Text;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Application.Users;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Logging;

namespace Agentd.Application.Messaging;

/// <summary>The tail of a job's agent transcript (implemented next to the Claude runner).</summary>
public interface ITranscriptReader
{
    /// <summary>The last <paramref name="lines"/> lines, or null when there is no transcript yet.</summary>
    Task<string?> TailAsync(WorkItemId workItem, int lines, CancellationToken cancellationToken);
}

/// <summary>
/// Neutral chat commands. In a job's thread: <c>status</c>, <c>cancel</c>, <c>retry</c>, <c>logs</c>.
/// Anywhere: <c>list</c>, <c>run &lt;workItemId&gt;</c>, <c>help</c>. Replies in a thread go through
/// the outbox to the same provider; outside a thread there is no job, so they are sent directly.
/// </summary>
public sealed partial class ChatCommands(
    IJobRepository jobs,
    IQueryHandler<GetJobStatus, IReadOnlyList<JobStatusRow>> status,
    ICommandHandler<CancelJob, Unit> cancel,
    ICommandHandler<RetryJob, int> retry,
    ICommandHandler<ClaimWorkItem, JobId> claim,
    ICommandHandler<StartHandoff, Unit> handoff,
    IConversationStore conversations,
    ITranscriptReader transcripts,
    IOutbox outbox,
    IMessagingProviderRegistry providers,
    ILogger<ChatCommands> logger,
    IEventStore? events = null,
    ICommandHandler<Permissions.PermissionAnswer, bool>? permissions = null,
    Ideas.IdeaService? ideas = null,
    Ideas.IIdeaStore? ideaStore = null,
    IRepositoryRegistry? repositories = null,
    ICommandHandler<Repositories.AddRepository, Domain.Repositories.Repository>? addRepository = null,
    ICommandHandler<Repositories.RemoveRepository, Unit>? removeRepository = null,
    ICommandHandler<PauseJob, Unit>? pause = null,
    ICommandHandler<ResumeJob, Unit>? resume = null,
    Reviews.ReviewService? reviews = null,
    Reviews.IReviewStore? reviewStore = null,
    JobActivity? activity = null,
    Domain.Common.IClock? clock = null)
{
    /// <summary>Commands typed in a job's thread are recorded, so the work item conversation shows both directions.</summary>
    public const string CommandEventType = "chat.command";

    public const int LogLines = 200;

    /// <summary>The command list alone (the reply to an unknown command).</summary>
    public static readonly string Commands =
        "In a job's thread: `status`, `logs`, `pause`, `resume`, `cancel`, `retry`, `handoff`, `approve`, `deny`. Anywhere: `list`, `run <id>`, `idea <text>`, `review <PR>`, `repo`, `help`. In idea/review threads: `model`, `effort`.";

    /// <summary>The reply to <c>idea</c> until brainstorming (Phase 2d) is built.</summary>
    public const string IdeaComingSoon =
        "💡 **Brainstorming is coming soon.** `idea <your idea>` will open a thread where the agent explores the idea against the code, " +
        "proposes User Stories and Tasks, and creates them in Azure DevOps when you agree. It isn't available yet; your idea wasn't saved, so post it again then.";

    /// <summary>Everything agentd does and how to talk to it (the reply to <c>help</c>). Commands start with the chat's prefix (<c>!</c> on Discord).</summary>
    public static readonly string Help = string.Join('\n',
        "**agentd: what I can do**",
        "",
        "**Commands** (start with `!` on Discord)",
        "In a job's thread:",
        "• `status`: phase, current activity, elapsed time and usage",
        "• `logs`: the agent's recent transcript",
        "• `pause` / `resume`: stop for now, continue later (`cancel` ends it)",
        "• `retry`: run a failed or cancelled job again",
        "• `handoff`: start the knowledge hand-off (after the PR is merged)",
        "• `approve [job|always]` / `deny`: answer a permission request",
        "Anywhere:",
        "• `list`: active jobs",
        "• `run <work item id>`: start a work item now, even without the tag",
        "• `idea <text>`: brainstorm into work items",
        "• `review <PR> [instructions]`: review a PR; you pick what's posted",
        "• `repo list|add <url>|remove <name>`: repositories (Admins change them)",
        "• `help`: this message",
        "",
        "**Talking to the agent** (in a job's thread)",
        "• Any other message goes to the agent: you get the status right away, and it reads your message at its next step.",
        "• Ask \"what's the progress?\" anytime.",
        "• Answer a question with its number (`1`, `2`, …) or in your own words.",
        "• 🔐 Permissions: `1` once, `2` this job, `3` always (repo), `4` deny (no answer in 10 min = deny).",
        "",
        "**The job cycle** (one thread per work item)",
        "1. I pick up work items tagged `ai-workflow`, or the one you `run`.",
        "2. Clarify, then **plan** with an estimate. Reply `1` to approve or say what to change (`ai-auto` skips this).",
        "3. Implement and verify, then open a **pull request**.",
        "4. **Review loop:** I fix PR comments, or your messages here, until the PR is ready to complete.",
        "5. After the merge, **hand-off:** I propose knowledge to sync into the repo; then I only answer questions.",
        "6. **Close-out:** I ask whether to delete this thread (`1` delete, `2` keep).",
        "",
        "**Along the way**",
        "• A heartbeat every minute, usage warnings at 80%, reminders while I wait for you",
        "• `model` / `effort`: change the model in an idea or review thread",
        "• Everything is also on the web dashboard.");

    public async Task<InboundOutcome> ExecuteAsync(InboundMessage message, AgentdUser user, Conversation? conversation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(user);
        var command = message.Command!;
        var name = command.Name.ToLowerInvariant();
        var job = conversation?.JobId;
        if (job is { } recorded && events is not null)
        {
            await RecordAsync(recorded, message, command, user, ct).ConfigureAwait(false);
        }

        OutboundMessage reply;
        switch (name)
        {
            case "list":
                reply = await ListAsync(message.Provider, ct).ConfigureAwait(false);
                break;
            case "run":
                reply = await RunAsync(command.Args, ct).ConfigureAwait(false);
                break;
            case "status" or "cancel" or "retry" or "logs" or "handoff" or "approve" or "deny" or "pause" or "resume" when job is null:
                reply = new(MessageKind.Info, $"`{name}` works in a job's thread. Use `list` to find one.");
                break;
            case "status":
                reply = await StatusAsync(job!.Value, ct).ConfigureAwait(false);
                break;
            case "cancel":
                var cancelled = await cancel.Handle(new CancelJob(job!.Value, user.Name), ct).ConfigureAwait(false);
                // On success the cancellation itself posts "cancelled by …" to every conversation.
                if (cancelled.IsSuccess)
                {
                    return new InboundOutcome("command:cancel", job);
                }

                reply = new(MessageKind.Info, $"Can't cancel: {cancelled.Error.Message}");
                break;
            case "pause" when pause is not null:
                return await PauseOrResumeAsync(message, job!.Value, "pause", await pause.Handle(new PauseJob(job.Value, user.Name), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            case "resume" when resume is not null:
                return await PauseOrResumeAsync(message, job!.Value, "resume", await resume.Handle(new ResumeJob(job.Value, user.Name), ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            case "retry":
                var retried = await retry.Handle(new RetryJob(job!.Value), ct).ConfigureAwait(false);
                reply = retried.IsSuccess
                    ? new(MessageKind.Info, string.Create(CultureInfo.InvariantCulture, $"Queued again (attempt {retried.Value})."))
                    : new(MessageKind.Info, $"Can't retry: {retried.Error.Message}");
                break;
            case "logs":
                reply = await LogsAsync(job!.Value, ct).ConfigureAwait(false);
                break;
            case "handoff":
                var started = await handoff.Handle(new StartHandoff(job!.Value), ct).ConfigureAwait(false);
                // On success the HandoffStarted event announces it in the thread.
                if (started.IsSuccess)
                {
                    return new InboundOutcome("command:handoff", job);
                }

                reply = new(MessageKind.Info, $"Can't start the hand-off: {started.Error.Message}");
                break;
            case "approve" or "deny":
                if (await AnswerPermissionAsync(job!.Value, name, command.Args, user, ct).ConfigureAwait(false) is not { } problem)
                {
                    return new InboundOutcome($"command:{name}", job);   // the decision is announced in the thread
                }

                reply = problem;
                break;
            case "idea":
                reply = await StartIdeaAsync(message, user, command.Args, ct).ConfigureAwait(false);
                break;
            case "review":
                reply = reviews is null
                    ? new(MessageKind.Info, "PR reviews aren't enabled.")
                    : await reviews.StartAsync(message.Provider, user.Name, command.Args, ct).ConfigureAwait(false) is var startedReview && startedReview.IsSuccess
                        ? new(MessageKind.Info, startedReview.Value)
                        : new(MessageKind.Info, startedReview.Error.Message);
                break;
            case "repo":
                reply = await RepoAsync(command.Args, user, ct).ConfigureAwait(false);
                break;
            case "model" or "effort":
                reply = await IdeaSettingsAsync(message, name, command.Args, ct).ConfigureAwait(false);
                break;
            default:
                reply = new(MessageKind.Info, name == "help" ? Help : $"Unknown command `{name}`. {Commands} Send `help` for everything I can do.");
                break;
        }

        await ReplyAsync(message, job, reply, ct).ConfigureAwait(false);
        return new InboundOutcome($"command:{(name is "list" or "run" or "status" or "cancel" or "retry" or "logs" or "handoff" or "approve" or "deny" or "idea" or "review" or "model" or "effort" or "repo" or "pause" or "resume" or "help" ? name : "unknown")}", job);
    }

    /// <summary><c>approve [request] [once|job|always]</c> or <c>deny [request]</c>; null when it was decided (announced in the thread).</summary>
    private async Task<OutboundMessage?> AnswerPermissionAsync(JobId job, string name, IReadOnlyList<string> args, AgentdUser user, CancellationToken ct)
    {
        if (permissions is null)
        {
            return new(MessageKind.Info, "Permission requests aren't enabled.");
        }

        long? id = args.Count > 0 && long.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        var scope = args.Skip(id is null ? 0 : 1).FirstOrDefault()?.ToLowerInvariant();
        var answer = name == "deny" ? "deny" : scope switch { "job" => "job", "always" or "repo" => "always", _ => "once" };
        var handled = await permissions.Handle(new Permissions.PermissionAnswer(job, answer, user.Name, id), ct).ConfigureAwait(false);
        return handled is { IsSuccess: true, Value: true } ? null : new(MessageKind.Info, "There's no open permission request to answer in this thread.");
    }

    /// <summary><c>idea [--repo r] [--model m] [--effort e] &lt;text&gt;</c>: opens a brainstorm thread.</summary>
    private async Task<OutboundMessage> StartIdeaAsync(InboundMessage message, AgentdUser user, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (ideas is null)
        {
            return new(MessageKind.Info, IdeaComingSoon);
        }

        var (text, repo, model, effort, problem) = Ideas.BrainstormSettings.Parse(args);
        if (problem is not null)
        {
            return new(MessageKind.Info, problem);
        }

        var started = await ideas.StartAsync(message.Provider, user.Name, text, repo, ct, model, effort).ConfigureAwait(false);
        return new(MessageKind.Info, started.IsSuccess ? started.Value : started.Error.Message);
    }

    /// <summary><c>model &lt;name&gt;</c> / <c>effort &lt;level&gt;</c> in an idea's thread.</summary>
    private async Task<OutboundMessage> IdeaSettingsAsync(InboundMessage message, string name, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (reviews is not null && reviewStore is not null
            && await reviewStore.FindByThreadAsync(message.Provider, message.ExternalConversationId, ct).ConfigureAwait(false) is { } review)
        {
            return new(MessageKind.Info, args.Count == 0
                ? $"Model **{review.Model ?? "default"}**, effort **{review.Effort ?? "default"}**. Change with `model <fable|opus|sonnet|full name>` or `effort <{string.Join("|", Ideas.BrainstormSettings.Efforts)}>`."
                : await reviews.ChangeSettingsAsync(review, name == "model" ? args[0] : null, name == "effort" ? args[0] : null, ct).ConfigureAwait(false));
        }

        if (ideas is null || ideaStore is null
            || await ideaStore.FindByThreadAsync(message.Provider, message.ExternalConversationId, ct).ConfigureAwait(false) is not { } idea)
        {
            return new(MessageKind.Info, $"`{name}` works in an idea's or review's thread (start one with `idea <text>` or `review <PR>`).");
        }

        if (args.Count == 0)
        {
            return new(MessageKind.Info, $"Model **{idea.Model ?? "default"}**, effort **{idea.Effort ?? "default"}**. Change with `model <fable|opus|sonnet|full name>` or `effort <{string.Join("|", Ideas.BrainstormSettings.Efforts)}>`.");
        }

        var text = await ideas.ChangeSettingsAsync(idea, name == "model" ? args[0] : null, name == "effort" ? args[0] : null, ct).ConfigureAwait(false);
        return new(MessageKind.Info, text);
    }

    /// <summary><c>repo list</c> · <c>repo add &lt;url&gt; [--name x] [--tag t] [--base b] [--area-path p]</c> · <c>repo remove &lt;name&gt;</c>. Add and remove need the Admin role.</summary>
    private async Task<OutboundMessage> RepoAsync(IReadOnlyList<string> args, AgentdUser user, CancellationToken ct)
    {
        if (repositories is null)
        {
            return new(MessageKind.Info, "Repository commands aren't available here.");
        }

        var sub = args.Count > 0 ? args[0].ToLowerInvariant() : "list";
        if (sub == "list")
        {
            var all = await repositories.ListAsync(ct).ConfigureAwait(false);
            return new(MessageKind.Info, all.Count == 0
                ? "No repositories are registered. An admin can add one with `!repo add <clone url>`."
                : "**Repositories**\n" + string.Join('\n', all.Select(r =>
                    $"• `{r.Name}`: {r.AzureDevOps.Organization}/{r.AzureDevOps.Project}/{r.AzureDevOps.Name} (base `{r.BaseBranch}`), matched by {Match(r)}")));
        }

        if (sub is not ("add" or "remove"))
        {
            return new(MessageKind.Info, "Use `repo list`, `repo add <clone url> [--name x] [--tag t] [--base b] [--area-path p]` or `repo remove <name>`.");
        }

        if (!user.Roles.Contains("Admin", StringComparer.OrdinalIgnoreCase))
        {
            return new(MessageKind.Info, $"Only an Admin can {sub} repositories (agents clone and work on them).");
        }

        if (sub == "remove")
        {
            if (removeRepository is null || args.Count < 2 || !RepositoryName.Create(args[1]).IsSuccess)
            {
                return new(MessageKind.Info, "Use `repo remove <name>`.");
            }

            var removed = await removeRepository.Handle(new Repositories.RemoveRepository(RepositoryName.From(args[1])), ct).ConfigureAwait(false);
            return new(MessageKind.Info, removed.IsSuccess ? $"🗑 Removed `{args[1]}` (its clone stays on disk; running jobs finish)." : removed.Error.Message);
        }

        if (addRepository is null || args.Count < 2)
        {
            return new(MessageKind.Info, "Use `repo add <clone url> [--name x] [--tag t] [--base b] [--area-path p]`.");
        }

        string? Option(string flag)
        {
            var at = args.ToList().FindIndex(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
        }

        var areas = args.Select((a, n) => (a, n)).Where(x => string.Equals(x.a, "--area-path", StringComparison.OrdinalIgnoreCase) && x.n + 1 < args.Count).Select(x => args[x.n + 1]).ToList();
        var added = await addRepository.Handle(new Repositories.AddRepository(args[1], Option("--name"), Option("--base"), Option("--tag"), areas), ct).ConfigureAwait(false);
        return new(MessageKind.Info, added.IsSuccess
            ? $"✅ Registered `{added.Value.Name}` (base `{added.Value.BaseBranch}`), matched by {Match(added.Value)}. Work items with that tag are picked up at the next poll."
            : $"Couldn't add it: {added.Error.Message}");
    }

    private static string Match(Domain.Repositories.Repository r) =>
        string.Join(" or ", new[] { r.MatchTag is { Length: > 0 } t ? $"tag `{t}`" : null }
            .Concat(r.MatchAreaPaths.Select(a => $"area `{a}`")).OfType<string>().DefaultIfEmpty("nothing (add a tag or area path)"));

    /// <summary>On success the paused / resumed notice is posted to every conversation; otherwise say why here.</summary>
    private async Task<InboundOutcome> PauseOrResumeAsync(InboundMessage message, JobId job, string name, Domain.Common.Result<Unit> changed, CancellationToken ct)
    {
        if (!changed.IsSuccess)
        {
            await ReplyAsync(message, job, new(MessageKind.Info, $"Can't {name}: {changed.Error.Message}"), ct).ConfigureAwait(false);
        }

        return new InboundOutcome($"command:{name}", job);
    }

    private async Task RecordAsync(JobId job, InboundMessage message, InboundCommand command, AgentdUser user, CancellationToken ct)
    {
        try
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(new { name = command.Name, args = command.Args, text = message.Text, user = user.Name, provider = message.Provider.Value });
            await events!.AppendAsync(job, CommandEventType, payload, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRecordFailed(logger, ex, command.Name);   // the command still runs
        }
    }

    private async Task<OutboundMessage> ListAsync(ProviderKey provider, CancellationToken ct)
    {
        var rows = await status.Handle(new GetJobStatus(), ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return new(MessageKind.Info, "No active jobs.");
        }

        var sb = new StringBuilder("**Active jobs**\n");
        foreach (var row in rows)
        {
            var thread = (await conversations.ListByJobAsync(new JobId(row.Id), ct).ConfigureAwait(false))
                .FirstOrDefault(c => c.Provider == provider && c.IsOpen)?.Link;
            sb.Append(CultureInfo.InvariantCulture, $"\n- #{row.WorkItemId} `{row.Repository}`: {row.State}, {Elapsed(row.Elapsed)}");
            if (thread is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $" ([thread]({thread}))");
            }
        }

        return new(MessageKind.Info, sb.ToString());
    }

    private async Task<OutboundMessage> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        if (args.Count != 1 || !int.TryParse(args[0].TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || WorkItemId.Create(id) is not { IsSuccess: true } workItem)
        {
            return new(MessageKind.Info, "Usage: `run <work item id>`");
        }

        var claimed = await claim.Handle(new ClaimWorkItem(workItem.Value, Force: true), ct).ConfigureAwait(false);
        return claimed.IsSuccess
            ? new(MessageKind.Info, $"Queued job #{claimed.Value} for work item #{id}.")
            : new(MessageKind.Info, $"Can't run #{id}: {claimed.Error.Message}");
    }

    private async Task<OutboundMessage> StatusAsync(JobId id, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct).ConfigureAwait(false);
        if (job is null)
        {
            return new(MessageKind.Info, "This job no longer exists.");
        }

        var sb = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"**#{job.WorkItemId} {job.Title}**\n\nState: **{job.State}**, attempt {job.Attempt}"));
        if (job.Branch is { } branch)
        {
            sb.Append(CultureInfo.InvariantCulture, $", branch `{branch}`");
        }

        if (job.PullRequest is { } pr)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n\nPull request: {pr.Value}");
        }
        else if (job.LastError is { } error)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n\nLast error: {error}");
        }

        if (activity is not null && job.State is Domain.Jobs.JobState.Running or Domain.Jobs.JobState.WaitingForHuman)
        {
            sb.Append("\n\nNow: ").Append(JobActivity.DescribeWithOutput(job, activity.Get(job.Id), clock?.UtcNow ?? DateTimeOffset.UtcNow));
        }

        if (job.PendingMessages.Count > 0)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n\n{job.PendingMessages.Count} message(s) queued for the agent's next turn.");
        }

        return new(MessageKind.Info, sb.ToString());
    }

    private async Task<OutboundMessage> LogsAsync(JobId id, CancellationToken ct)
    {
        var job = await jobs.GetAsync(id, ct).ConfigureAwait(false);
        var tail = job is null ? null : await transcripts.TailAsync(job.WorkItemId, LogLines, ct).ConfigureAwait(false);
        return tail is null
            ? new(MessageKind.Info, "No transcript yet.")
            : new(MessageKind.Info, string.Create(CultureInfo.InvariantCulture, $"The last {LogLines} lines of the agent transcript are attached."), null,
                [new Attachment("transcript-tail.jsonl", "application/x-ndjson", Encoding.UTF8.GetBytes(tail))]);
    }

    private async Task ReplyAsync(InboundMessage message, JobId? job, OutboundMessage reply, CancellationToken ct)
    {
        if (job is { } id)
        {
            await outbox.EnqueueAsync(id, [new OutboxMessage(reply, new EnqueueOptions(OnlyProviders: [message.Provider]))], ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var provider = providers.Resolve(message.Provider);
            foreach (var part in MessageChunker.Prepare(reply, provider.Capabilities))
            {
                await provider.SendAsync(new ConversationRef(message.Provider, message.ExternalConversationId, null), part, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogReplyFailed(logger, ex, message.Provider.Value);
        }
    }

    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}h{t.Minutes:00}m")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalMinutes}m");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording the {Command} command failed")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string command);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replying to a {Provider} command outside a job thread failed")]
    private static partial void LogReplyFailed(ILogger logger, Exception exception, string provider);
}
