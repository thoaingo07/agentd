using Agentd.Application.Abstractions;
using Agentd.Application.Events;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Queries;

/// <summary>A job as screens list it (dashboard, history).</summary>
public sealed record JobSummary(
    long Id,
    int WorkItemId,
    string Title,
    string Repository,
    string? Branch,
    JobState State,
    string? Phase,
    DateTimeOffset StartedAt,
    TimeSpan Elapsed,
    string? PullRequestUrl,
    DateTimeOffset? WaitingSince,
    PlanStatus PlanStatus,
    HandoffStatus Handoff,
    int FixRounds,
    string? LastError,
    DateTimeOffset? CompletedAt = null);

/// <summary>A job's detail page: the summary plus its threads, plan estimate and live activity.</summary>
public sealed record JobDetail(
    JobSummary Summary,
    int Attempt,
    int ResumeCount,
    int PendingMessages,
    PlanEstimate? Estimate,
    string? LastActivity,
    DateTimeOffset? LastActivityAt,
    UsageSnapshot? Usage,
    IReadOnlyList<ConversationLink> Conversations);

public sealed record ConversationLink(string Provider, Uri? Link, bool Open);

/// <summary>The dashboard snapshot.</summary>
/// <param name="CountsByState">Active jobs per state.</param>
/// <param name="ActiveJobs">Waiting jobs first.</param>
/// <param name="LatestSeq">The event stream position this snapshot is at least as new as: stream "all" from here.</param>
public sealed record Dashboard(IReadOnlyDictionary<JobState, int> CountsByState, IReadOnlyList<JobSummary> ActiveJobs, long LatestSeq);

public sealed record EventPage(IReadOnlyList<AgentEventDto> Events, long? OldestSeq, long? NewestSeq, bool HasMore);

/// <summary>A page of history; <paramref name="TotalCapped"/> means there are more than <paramref name="Total"/> matches.</summary>
/// <param name="Items">The page, newest first.</param>
/// <param name="Total">Matches, at most <see cref="SearchHistoryHandler.CountCap"/>.</param>
/// <param name="TotalCapped">The real count is higher.</param>
/// <param name="Page">1-based.</param>
/// <param name="PageSize">Items per page.</param>
public sealed record HistoryPage(IReadOnlyList<JobSummary> Items, long Total, bool TotalCapped, int Page, int PageSize);

public sealed record GetDashboard;

public sealed record GetJob(JobId JobId);

/// <summary>A page of a job's events: after <see cref="After"/> (live catch-up) or before <see cref="Before"/> (load earlier), never both.</summary>
public sealed record GetJobEvents(JobId JobId, long? After, long? Before, int Limit);

/// <summary>One event of a job in full (the live stream trims big payloads).</summary>
public sealed record GetJobEventDetail(JobId JobId, long Seq);

/// <summary>History: <c>Text</c> is a title substring, or <c>WI-1234</c> for exactly that work item (plain digits match either).</summary>
public sealed record SearchHistory(IReadOnlyCollection<JobState>? States, string? Repository, string? Text, int Page, int PageSize, DateTimeOffset? From = null, DateTimeOffset? To = null);

internal static class JobViews
{
    public static readonly JobState[] Active =
        [JobState.WaitingForHuman, JobState.Running, JobState.Preparing, JobState.Queued, JobState.Publishing, JobState.InReview, JobState.Paused];

    public static JobSummary Summary(Job job, JobActivity activity, DateTimeOffset now) => new(
        job.Id.Value, job.WorkItemId.Value, job.Title, job.Repository.Value, job.Branch?.Value, job.State,
        activity.Get(job.Id).Phase, job.CreatedAt, (job.State.IsTerminal() ? job.UpdatedAt : now) - job.CreatedAt,
        job.PullRequest?.Value.ToString(), job.WaitingSince, job.PlanStatus, job.Handoff, job.FixRounds, job.LastError,
        job.State.IsTerminal() ? job.UpdatedAt : null);
}

/// <summary>Active jobs (those waiting for a human first) and job counts by state.</summary>
public sealed class GetDashboardHandler(IJobRepository jobs, IEventReader events, JobActivity activity, IClock clock) : IQueryHandler<GetDashboard, Dashboard>
{
    public async Task<Dashboard> Handle(GetDashboard query, CancellationToken cancellationToken)
    {
        // Read the position first: events committed while the jobs are listed are replayed, never skipped.
        var latest = await events.LatestSeqAsync(cancellationToken).ConfigureAwait(false);
        var active = await jobs.ListByStateAsync(JobViews.Active, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var ordered = active.OrderBy(j => Array.IndexOf(JobViews.Active, j.State)).ThenBy(j => j.CreatedAt)
            .Select(j => JobViews.Summary(j, activity, now)).ToList();
        return new Dashboard(active.GroupBy(j => j.State).ToDictionary(g => g.Key, g => g.Count()), ordered, latest);
    }
}

public sealed class GetJobHandler(IJobRepository jobs, IConversationStore conversations, JobActivity activity, IClock clock)
    : IQueryHandler<GetJob, JobDetail?>
{
    public async Task<JobDetail?> Handle(GetJob query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await jobs.GetAsync(query.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return null;
        }

        var live = activity.Get(job.Id);
        var threads = await conversations.ListByJobAsync(job.Id, cancellationToken).ConfigureAwait(false);
        return new JobDetail(
            JobViews.Summary(job, activity, clock.UtcNow), job.Attempt, job.ResumeCount, job.PendingMessages.Count, job.Estimate,
            live.LastActivity, live.LastActivityAt, live.Usage,
            threads.Select(c => new ConversationLink(c.Provider.Value, c.Link, c.IsOpen)).ToList());
    }
}

public sealed class GetJobEventsHandler(IEventReader events) : IQueryHandler<GetJobEvents, EventPage>
{
    public async Task<EventPage> Handle(GetJobEvents query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Ask for one more than the limit to know whether there is more in that direction.
        var page = query.Before is { } before
            ? await events.ReadBeforeAsync(query.JobId, before, query.Limit + 1, cancellationToken).ConfigureAwait(false)
            : await events.ReadAfterAsync(query.JobId, query.After ?? 0, query.Limit + 1, cancellationToken).ConfigureAwait(false);
        var hasMore = page.Count > query.Limit;
        var trimmed = hasMore ? (query.Before is null ? page.Take(query.Limit) : page.Skip(1)).ToList() : page.ToList();
        return new EventPage(trimmed, trimmed.Count > 0 ? trimmed[0].Seq : null, trimmed.Count > 0 ? trimmed[^1].Seq : null, hasMore);
    }
}

public sealed class GetJobEventDetailHandler(IEventReader events) : IQueryHandler<GetJobEventDetail, AgentEventDto?>
{
    public async Task<AgentEventDto?> Handle(GetJobEventDetail query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var found = await events.GetAsync(query.Seq, cancellationToken).ConfigureAwait(false);
        return found?.JobId == query.JobId.Value ? found : null;
    }
}

public sealed partial class SearchHistoryHandler(IJobSearch search, JobActivity activity, IClock clock) : IQueryHandler<SearchHistory, HistoryPage>
{
    /// <summary>Counting stops here; the UI shows "10,000+".</summary>
    public const int CountCap = 10_000;

    public async Task<HistoryPage> Handle(SearchHistory query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var repository = string.IsNullOrWhiteSpace(query.Repository) ? (RepositoryName?)null : RepositoryName.From(query.Repository);
        var (title, workItem) = ParseText(query.Text);
        var filter = new JobSearchFilter(query.States, repository, title, workItem, query.From, query.To);
        var (found, total) = await search.SearchAsync(filter, (query.Page - 1) * query.PageSize, query.PageSize, CountCap, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        return new HistoryPage(found.Select(j => JobViews.Summary(j, activity, now)).ToList(), Math.Min(total, CountCap), total > CountCap, query.Page, query.PageSize);
    }

    /// <summary>"WI-1234" → that work item only; "1234" → the title or the work item; anything else → the title.</summary>
    internal static (string? Title, WorkItemId? WorkItem) ParseText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return (null, null);
        }

        var trimmed = text.Trim();
        var match = WorkItemPattern().Match(trimmed);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            return (trimmed, null);
        }

        return (match.Groups["prefix"].Success ? null : trimmed, WorkItemId.From(id));
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<prefix>WI-?)?(?<id>\d{1,9})$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex WorkItemPattern();
}
