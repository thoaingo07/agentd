using System.Text.Json;
using Agentd.Application.Events;
using Agentd.Application.Ports;
using Agentd.Application.Queries;

namespace Agentd.Bff.ViewModels;

/// <summary>A job row (dashboard, history). State and statuses are strings for the UI.</summary>
public sealed record JobSummaryVm(
    long Id,
    int WorkItemId,
    string Title,
    string Repo,
    string? Branch,
    string State,
    string? Phase,
    DateTimeOffset StartedAt,
    long ElapsedSeconds,
    string? PrUrl,
    DateTimeOffset? WaitingSince,
    string PlanStatus,
    string Handoff,
    int FixRounds,
    string? LastError,
    DateTimeOffset? CompletedAt,
    int PendingPermissions)
{
    public static JobSummaryVm From(JobSummary s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Id, s.WorkItemId, s.Title, s.Repository, s.Branch, s.State.ToString(), s.Phase, s.StartedAt, (long)s.Elapsed.TotalSeconds,
            s.PullRequestUrl, s.WaitingSince, s.PlanStatus.ToString(), s.Handoff.ToString(), s.FixRounds, s.LastError, s.CompletedAt, s.PendingPermissions);
    }
}

public sealed record DashboardVm(IReadOnlyDictionary<string, int> Stats, IReadOnlyList<JobSummaryVm> ActiveJobs, long LatestSeq)
{
    public static DashboardVm From(Dashboard d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new(d.CountsByState.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value), d.ActiveJobs.Select(JobSummaryVm.From).ToList(), d.LatestSeq);
    }
}

public sealed record EstimateVm(int Minutes, int UsagePercent, double? UsageAtPlan, DateTimeOffset SubmittedAt, DateTimeOffset? ApprovedAt);

public sealed record UsageVm(double? FiveHour, double? Weekly, DateTimeOffset? ResetsAt);

/// <summary>A tool call outside the agent's allowlist, waiting for a person (<c>ruleKeys</c>: what "allow for this job / always" remembers).</summary>
public sealed record PermissionRequestVm(long Id, string Tool, string Summary, IReadOnlyList<string> RuleKeys, DateTimeOffset RequestedAt);

/// <summary>A remembered approval; <c>jobId</c> null means every job of the repository.</summary>
public sealed record PermissionRuleVm(long Id, string Repo, long? JobId, string RuleKey, string CreatedBy, DateTimeOffset CreatedAt);

public sealed record ConversationVm(string Provider, Uri? Link, bool Open);

public sealed record JobDetailVm(
    JobSummaryVm Job,
    int Attempt,
    int ResumeCount,
    int PendingMessages,
    EstimateVm? Estimate,
    string? LastActivity,
    DateTimeOffset? LastActivityAt,
    UsageVm? Usage,
    IReadOnlyList<ConversationVm> Conversations,
    IReadOnlyList<PermissionRequestVm> Permissions)
{
    public static JobDetailVm From(JobDetail d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return new(
            JobSummaryVm.From(d.Summary), d.Attempt, d.ResumeCount, d.PendingMessages,
            d.Estimate is { } e ? new EstimateVm(e.Minutes, e.UsagePercent, e.UsageAtPlan, e.SubmittedAt, e.ApprovedAt) : null,
            d.LastActivity, d.LastActivityAt,
            d.Usage is { } u ? new UsageVm(u.FiveHour, u.Weekly, u.ResetsAt) : null,
            d.Conversations.Select(c => new ConversationVm(c.Provider, c.Link, c.Open)).ToList(),
            (d.Permissions ?? []).Select(p => new PermissionRequestVm(p.Id, p.ToolName, p.Summary, p.RuleKeys, p.RequestedAt)).ToList());
    }
}

/// <summary>One event; the payload is JSON passed through (it was redacted when stored).</summary>
public sealed record EventVm(long Seq, long? JobId, DateTimeOffset Ts, string Type, JsonElement Payload)
{
    /// <summary>The live stream's limit per payload; bigger ones become <c>{ "truncated": true, "bytes": n }</c>.</summary>
    public const int MaxStreamedPayloadBytes = 64 * 1024;

    public static EventVm From(AgentEventDto e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.Seq, e.JobId, e.Ts, e.Type, e.Payload);
    }

    /// <summary>For the live stream: a payload over <see cref="MaxStreamedPayloadBytes"/> is replaced; the UI loads it with <c>GET /api/jobs/{id}/events/{seq}</c>.</summary>
    public static EventVm ForStream(AgentEventDto e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var bytes = System.Text.Encoding.UTF8.GetByteCount(e.Payload.GetRawText());
        return bytes <= MaxStreamedPayloadBytes
            ? From(e)
            : new(e.Seq, e.JobId, e.Ts, e.Type, JsonSerializer.SerializeToElement(new { truncated = true, bytes }));
    }
}

public sealed record EventPageVm(IReadOnlyList<EventVm> Events, long? OldestSeq, long? NewestSeq, bool HasMore)
{
    public static EventPageVm From(EventPage p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Events.Select(EventVm.From).ToList(), p.OldestSeq, p.NewestSeq, p.HasMore);
    }
}

/// <summary>A history page; <c>totalCapped</c> means "more than <c>total</c>" (shown as 10,000+).</summary>
public sealed record HistoryPageVm(IReadOnlyList<JobSummaryVm> Items, long Total, bool TotalCapped, int Page, int PageSize)
{
    public static HistoryPageVm From(HistoryPage p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.Items.Select(JobSummaryVm.From).ToList(), p.Total, p.TotalCapped, p.Page, p.PageSize);
    }
}

public sealed record MessageAcceptedVm(string Outcome);

public sealed record RunAcceptedVm(long JobId);

/// <summary>A job's changes; <c>unifiedDiff</c> is null when <c>truncated</c> (over 2 MB), leaving the file list.</summary>
public sealed record DiffVm(string BaseRef, string HeadRef, IReadOnlyList<string> Files, string? UnifiedDiff, bool Truncated)
{
    public static DiffVm From(BranchDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        return new(diff.BaseRef, diff.HeadRef, diff.Files, diff.UnifiedDiff, diff.Truncated);
    }
}

public sealed record AntiforgeryTokenVm(string Token);

/// <summary>The signed-in user; in Mode None, <c>{ name: "local", roles: ["Admin"], provider: "local" }</c>.</summary>
public sealed record UserVm(string Name, IReadOnlyList<string> Roles, string Provider);

public sealed record RepositoryVm(string Name, string Organization, string Project, string BaseBranch);

/// <summary>Read-only settings. Explicit fields only: tokens, keys and secrets have no way in.</summary>
public sealed record ConfigVm(
    string Tag,
    string ClaimTag,
    long PollIntervalSeconds,
    int MaxConcurrent,
    bool RequirePlanApproval,
    bool ReviewLoop,
    bool Handoff,
    IReadOnlyList<RepositoryVm> Repositories,
    IReadOnlyList<string> MessagingProviders)
{
    public static ConfigVm From(ConfigSummary c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.Tag, c.ClaimTag, (long)c.PollInterval.TotalSeconds, c.MaxConcurrent, c.RequirePlanApproval, c.ReviewLoop, c.Handoff,
            c.Repositories.Select(r => new RepositoryVm(r.Name, r.Organization, r.Project, r.BaseBranch)).ToList(), c.MessagingProviders);
    }
}

public sealed record PullRequestLinkVm(long JobId, string Url);

/// <summary>One work item across its jobs (oldest first), with its PRs and chat threads (open or closed).</summary>
public sealed record WorkItemVm(
    int WorkItemId,
    string Title,
    string Repo,
    IReadOnlyList<JobSummaryVm> Jobs,
    IReadOnlyList<PullRequestLinkVm> PullRequests,
    IReadOnlyList<ConversationVm> Conversations,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastActivityAt)
{
    public static WorkItemVm From(WorkItemSummary w)
    {
        ArgumentNullException.ThrowIfNull(w);
        return new(w.WorkItemId, w.Title, w.Repository, w.Jobs.Select(JobSummaryVm.From).ToList(),
            w.PullRequests.Select(p => new PullRequestLinkVm(p.JobId, p.Url)).ToList(),
            w.Conversations.Select(c => new ConversationVm(c.Provider, c.Link, c.Open)).ToList(),
            w.FirstSeenAt, w.LastActivityAt);
    }
}

/// <summary>A conversation line: <c>direction</c> is <c>out</c> (agentd) or <c>in</c> (a developer's reply or command, by <c>author</c>).</summary>
public sealed record ConversationEntryVm(DateTimeOffset At, long? JobId, string Direction, string Kind, string Text, string? Provider, string? Author, string? Status, string? Error)
{
    public static ConversationEntryVm From(ConversationEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new(e.At, e.JobId, e.Direction, e.Kind, e.Text, e.Provider, e.From, e.Status, e.Error);
    }
}
