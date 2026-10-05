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
    ILogger<ChatCommands> logger)
{
    public const int LogLines = 200;

    /// <summary>The command list alone (the reply to an unknown command).</summary>
    public static readonly string Commands =
        "In a job's thread: `status`, `logs`, `cancel`, `retry`, `handoff`. Anywhere: `list`, `run <work item id>`, `help`.";

    /// <summary>Everything agentd does and how to talk to it (the reply to <c>help</c>). Commands start with the chat's prefix (<c>!</c> on Discord).</summary>
    public static readonly string Help = string.Join('\n',
        "**agentd: what I can do**",
        "",
        "**Commands** (start with `!` on Discord)",
        "In a job's thread:",
        "• `status`: phase, current activity, elapsed time and usage",
        "• `logs`: the last " + LogLines + " lines of the agent's transcript",
        "• `cancel`: stop the job",
        "• `retry`: run a failed job again",
        "• `handoff`: start the knowledge hand-off (after the PR is merged)",
        "Anywhere:",
        "• `list`: active jobs",
        "• `run <work item id>`: start a work item now, even without the tag",
        "• `help`: this message",
        "",
        "**Talking to the agent** (in a job's thread)",
        "• Any other message goes to the agent. While it's working you get the current status right away, and it reads your message at its next step.",
        "• Ask \"what's the progress?\" at any time.",
        "• Answer a question with its number (`1`, `2`, …) or in your own words.",
        "",
        "**The job cycle** (one thread per work item)",
        "1. I pick up work items tagged `ai-workflow`, or the one you `run`.",
        "2. Clarify, then **plan**, with a time and usage estimate. Reply `1` or `approve`, or say what to change. The `ai-auto` tag skips the approval.",
        "3. Implement and verify, then open a **pull request**.",
        "4. **Review loop:** I fix PR comments and reply in their threads until the PR is ready to complete.",
        "5. After the merge, **hand-off:** I propose the knowledge and learnings to sync into the repo. Agree, ask for changes, or decline.",
        "6. **Close-out:** I ask whether to delete this thread (`1` delete, `2` keep).",
        "",
        "**Along the way**",
        "• A heartbeat in the thread every minute while I work, with the timestamp",
        "• A warning when usage reaches 80% of the 5-hour or weekly window",
        "• Reminders while I'm waiting for you",
        "• Everything is also on the web dashboard.");

    public async Task<InboundOutcome> ExecuteAsync(InboundMessage message, AgentdUser user, Conversation? conversation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(user);
        var command = message.Command!;
        var name = command.Name.ToLowerInvariant();
        var job = conversation?.JobId;
        OutboundMessage reply;
        switch (name)
        {
            case "list":
                reply = await ListAsync(message.Provider, ct).ConfigureAwait(false);
                break;
            case "run":
                reply = await RunAsync(command.Args, ct).ConfigureAwait(false);
                break;
            case "status" or "cancel" or "retry" or "logs" or "handoff" when job is null:
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
            default:
                reply = new(MessageKind.Info, name == "help" ? Help : $"Unknown command `{name}`. {Commands} Send `help` for everything I can do.");
                break;
        }

        await ReplyAsync(message, job, reply, ct).ConfigureAwait(false);
        return new InboundOutcome($"command:{(name is "list" or "run" or "status" or "cancel" or "retry" or "logs" or "handoff" or "help" ? name : "unknown")}", job);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Replying to a {Provider} command outside a job thread failed")]
    private static partial void LogReplyFailed(ILogger logger, Exception exception, string provider);
}
