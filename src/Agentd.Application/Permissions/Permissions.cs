using System.Collections.Concurrent;
using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Permissions;

/// <summary>A tool call waiting for (or decided by) a person.</summary>
public sealed record PermissionRequest(
    long Id, JobId JobId, string ToolName, string Summary, IReadOnlyList<string> RuleKeys, string Status, string? Scope, string? DecidedBy, DateTimeOffset RequestedAt);

/// <summary>A remembered approval: for one job (<paramref name="JobId"/> set) or every job of a repository.</summary>
public sealed record PermissionRule(long Id, string Repository, JobId? JobId, string RuleKey, string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>Permission requests and remembered approvals (PostgreSQL routines).</summary>
public interface IPermissionStore
{
    Task<long> InsertAsync(JobId jobId, string toolName, string summary, IReadOnlyList<string> ruleKeys, CancellationToken cancellationToken);

    /// <summary>Decides a pending request; null when it was already decided (the first answer wins).</summary>
    Task<PermissionRequest?> DecideAsync(long id, string status, string? scope, string decidedBy, RepositoryName repository, CancellationToken cancellationToken);

    Task<PermissionRequest?> GetAsync(long id, CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionRequest>> ListPendingAsync(JobId jobId, CancellationToken cancellationToken);

    /// <summary>Rule keys remembered for the job and its repository.</summary>
    Task<IReadOnlyList<string>> RuleKeysAsync(RepositoryName repository, JobId jobId, CancellationToken cancellationToken);

    /// <summary>Open requests per job; jobs without any are left out.</summary>
    Task<IReadOnlyDictionary<JobId, int>> PendingCountsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<PermissionRule>> ListRulesAsync(CancellationToken cancellationToken);

    /// <summary>Revokes a remembered approval; false when it's already gone.</summary>
    Task<bool> DeleteRuleAsync(long id, string by, CancellationToken cancellationToken);
}

/// <summary>The agent runner's own allowlist (Claude Code's <c>--allowedTools</c>), so already-allowed parts aren't asked about again.</summary>
public interface IToolAllowlist
{
    bool IsAllowed(string ruleKey);
}

/// <summary>Wakes the waiting tool call when a request is decided (same process); the database is the source of truth.</summary>
public sealed class PermissionWaiter
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource> _waiting = new();

    public void Signal(long id)
    {
        if (_waiting.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult();
        }
    }

    /// <summary>Waits for a signal or <paramref name="timeout"/>, re-checking the database every <paramref name="poll"/>.</summary>
    public async Task<PermissionRequest?> WaitAsync(long id, IPermissionStore store, TimeSpan timeout, TimeSpan poll, TimeProvider time, CancellationToken ct)
    {
        var deadline = time.GetUtcNow() + timeout;
        try
        {
            while (true)
            {
                var tcs = _waiting.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                var current = await store.GetAsync(id, ct).ConfigureAwait(false);
                if (current is null || current.Status != "pending")
                {
                    return current;
                }

                var left = deadline - time.GetUtcNow();
                if (left <= TimeSpan.Zero)
                {
                    return current;
                }

                await Task.WhenAny(tcs.Task, Task.Delay(left < poll ? left : poll, time, ct)).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }
}

/// <summary>The decision handed back to the agent runner.</summary>
public sealed record PermissionDecision(bool Allowed, string Message);

/// <summary>Called by the runner's permission prompt (MCP) when the agent wants a tool call outside its allowlist.</summary>
public sealed record PermissionAsk(JobId JobId, string ToolName, string InputJson);

/// <summary>
/// Hard denies first; then remembered approvals (this job, this repository); then, in <see cref="PermissionMode.Auto"/>,
/// allow without asking; otherwise ask in the job's chat thread (and the Web UI) and wait. No answer within <see cref="JobOptions.PermissionTimeout"/> is a deny, so
/// a job never hangs on a question nobody sees.
/// </summary>
public sealed class PermissionAskHandler(
    IJobRepository jobs,
    IPermissionStore store,
    IToolAllowlist allowlist,
    PermissionWaiter waiter,
    IOutbox outbox,
    JobActivity activity,
    IClock clock,
    TimeProvider time,
    IOptions<JobOptions> options) : ICommandHandler<PermissionAsk, PermissionDecision>
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>Who "decided" a request in <see cref="PermissionMode.Auto"/>.</summary>
    public const string AutoDecider = "auto";

    public async Task<Result<PermissionDecision>> Handle(PermissionAsk command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        var analysis = PermissionRules.Analyze(command.ToolName, command.InputJson);
        if (analysis.HardDeny is { } reason)
        {
            return new PermissionDecision(false, $"agentd never allows this ({reason}). Find another way, or ask the developer with ask_developer.");
        }

        var needed = analysis.RuleKeys.Where(k => !PermissionRules.Covers(allowlist.IsAllowed, k)).ToList();
        var remembered = (await store.RuleKeysAsync(job.Repository, job.Id, cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        if (needed.All(k => PermissionRules.Covers(remembered.Contains, k)) && needed.Count > 0)
        {
            return new PermissionDecision(true, "Allowed by a remembered approval.");
        }

        if (options.Value.PermissionMode == PermissionMode.Auto)
        {
            // Still a request and a decision in the database (permission.requested / .decided events), so the
            // Web UI timeline shows what the agent was allowed to do.
            var allowed = await store.InsertAsync(job.Id, command.ToolName, analysis.Summary, analysis.RuleKeys, cancellationToken).ConfigureAwait(false);
            await store.DecideAsync(allowed, "allowed", "once", AutoDecider, job.Repository, cancellationToken).ConfigureAwait(false);
            activity.RecordActivity(job.Id, $"✅ auto-allowed: {Short(analysis.Summary)}", clock.UtcNow);
            return new PermissionDecision(true, "Allowed (agentd runs in auto permission mode).");
        }

        var timeout = options.Value.PermissionTimeout;
        var id = await store.InsertAsync(job.Id, command.ToolName, analysis.Summary, needed.Count > 0 ? needed : analysis.RuleKeys, cancellationToken).ConfigureAwait(false);
        await outbox.TryEnqueueAsync(job.Id, Question(id, analysis.Summary, job.Repository, timeout), cancellationToken).ConfigureAwait(false);
        activity.RecordActivity(job.Id, $"⏳ waiting for permission: {Short(analysis.Summary)}", clock.UtcNow);

        var decided = await waiter.WaitAsync(id, store, timeout, PollInterval, time, cancellationToken).ConfigureAwait(false);
        if (decided is { Status: "pending" })
        {
            decided = await store.DecideAsync(id, "expired", null, "timeout", job.Repository, cancellationToken).ConfigureAwait(false)
                ?? await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
            if (decided is { Status: "expired" })
            {
                await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info,
                    $"⌛ No answer within {Minutes(timeout)}: **denied** `{Short(analysis.Summary)}`. The agent carries on without it."), cancellationToken).ConfigureAwait(false);
            }
        }

        return decided is { Status: "allowed" }
            ? new PermissionDecision(true, $"Allowed by {decided.DecidedBy}.")
            : new PermissionDecision(false, decided is { Status: "denied" }
                ? $"Denied by {decided.DecidedBy}. Don't retry this; find another way or ask the developer with ask_developer."
                : $"Nobody answered within {Minutes(timeout)}, so it was denied. Find another way, or ask the developer with ask_developer.");
    }

    public static OutboundMessage Question(long id, string summary, RepositoryName repo, TimeSpan timeout) => new(
        MessageKind.Question,
        $"🔐 **Permission needed** (request {id})\nThe agent wants to run:\n```\n{Short(summary, 1500)}\n```\n" +
        $"Reply **1** allow once · **2** allow for this job · **3** always allow in `{repo}` · **4** deny. No answer in {Minutes(timeout)} = deny.",
        [new MessageOption("perm-once", PermissionAnswerHandler.LabelOnce), new MessageOption("perm-job", PermissionAnswerHandler.LabelJob),
         new MessageOption("perm-repo", PermissionAnswerHandler.LabelRepo), new MessageOption("perm-deny", PermissionAnswerHandler.LabelDeny)]);

    internal static string Short(string text, int max = 120) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Minutes(TimeSpan t) => t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";
}

/// <summary>A person answers a permission request: from a thread reply, <c>!approve</c> / <c>!deny</c>, or the Web UI.</summary>
/// <param name="JobId">The job (its latest open request when <paramref name="RequestId"/> is null).</param>
/// <param name="Answer">"1"–"4", a label, or a word (allow, yes, always, deny, no…).</param>
/// <param name="By">Who answered.</param>
/// <param name="RequestId">A specific request.</param>
public sealed record PermissionAnswer(JobId JobId, string Answer, string By, long? RequestId = null);

/// <summary>Returns false when there is no open request or the text isn't an answer (then it's an ordinary message).</summary>
public sealed class PermissionAnswerHandler(IJobRepository jobs, IPermissionStore store, PermissionWaiter waiter, IOutbox outbox)
    : ICommandHandler<PermissionAnswer, bool>
{
    public const string LabelOnce = "Allow once";
    public const string LabelJob = "Allow for this job";
    public const string LabelRepo = "Always allow in this repo";
    public const string LabelDeny = "Deny";

    public async Task<Result<bool>> Handle(PermissionAnswer command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Parse(command.Answer) is not { } choice)
        {
            return false;
        }

        var pending = await store.ListPendingAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        var request = command.RequestId is { } id ? pending.FirstOrDefault(p => p.Id == id) : pending.Count > 0 ? pending[^1] : null;
        if (request is null || await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return false;
        }

        var (status, scope) = choice;
        var decided = await store.DecideAsync(request.Id, status, scope, command.By, job.Repository, cancellationToken).ConfigureAwait(false);
        waiter.Signal(request.Id);
        if (decided is null)
        {
            return true;   // someone else answered first; their decision stands
        }

        var what = status == "denied" ? "⛔ **Denied**" : scope switch
        {
            "job" => "✅ **Allowed for this job**",
            "repo" => $"✅ **Always allowed in `{job.Repository}`**",
            _ => "✅ **Allowed once**",
        };
        await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, $"{what} by {command.By}: `{PermissionAskHandler.Short(request.Summary)}`"), cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The answer's status and scope, or null if the text isn't an answer.</summary>
    internal static (string Status, string? Scope)? Parse(string? answer)
    {
        var text = (answer ?? string.Empty).Trim().TrimEnd('.', '!').ToLowerInvariant();
        return text switch
        {
            "1" or "allow" or "allow once" or "once" or "yes" or "y" or "ok" or "okay" or "approve" => ("allowed", "once"),
            "2" or "allow for this job" or "job" or "allow job" => ("allowed", "job"),
            "3" or "always" or "always allow" or "always allow in this repo" or "repo" => ("allowed", "repo"),
            "4" or "deny" or "no" or "n" or "reject" => ("denied", null),
            _ => null,
        };
    }
}
