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
    string? LastError);

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

public sealed record Dashboard(IReadOnlyDictionary<JobState, int> CountsByState, IReadOnlyList<JobSummary> ActiveJobs);

public sealed record EventPage(IReadOnlyList<AgentEventDto> Events, long? OldestSeq, long? NewestSeq, bool HasMore);

public sealed record HistoryPage(IReadOnlyList<JobSummary> Items, long Total, int Page, int PageSize);

public sealed record GetDashboard;

public sealed record GetJob(JobId JobId);

/// <summary>A page of a job's events: after <see cref="After"/> (live catch-up) or before <see cref="Before"/> (load earlier), never both.</summary>
public sealed record GetJobEvents(JobId JobId, long? After, long? Before, int Limit);

public sealed record SearchHistory(IReadOnlyCollection<JobState>? States, string? Repository, string? Text, int Page, int PageSize);

internal static class JobViews
{
    public static readonly JobState[] Active =
        [JobState.WaitingForHuman, JobState.Running, JobState.Preparing, JobState.Queued, JobState.Publishing, JobState.InReview];

    public static JobSummary Summary(Job job, JobActivity activity, DateTimeOffset now) => new(
        job.Id.Value, job.WorkItemId.Value, job.Title, job.Repository.Value, job.Branch?.Value, job.State,
        activity.Get(job.Id).Phase, job.CreatedAt, (job.State.IsTerminal() ? job.UpdatedAt : now) - job.CreatedAt,
        job.PullRequest?.Value.ToString(), job.WaitingSince, job.PlanStatus, job.Handoff, job.FixRounds, job.LastError);
}

/// <summary>Active jobs (those waiting for a human first) and job counts by state.</summary>
public sealed class GetDashboardHandler(IJobRepository jobs, JobActivity activity, IClock clock) : IQueryHandler<GetDashboard, Dashboard>
{
    public async Task<Dashboard> Handle(GetDashboard query, CancellationToken cancellationToken)
    {
        var active = await jobs.ListByStateAsync(JobViews.Active, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var ordered = active.OrderBy(j => Array.IndexOf(JobViews.Active, j.State)).ThenBy(j => j.CreatedAt)
            .Select(j => JobViews.Summary(j, activity, now)).ToList();
        return new Dashboard(active.GroupBy(j => j.State).ToDictionary(g => g.Key, g => g.Count()), ordered);
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

public sealed class SearchHistoryHandler(IJobSearch search, JobActivity activity, IClock clock) : IQueryHandler<SearchHistory, HistoryPage>
{
    public async Task<HistoryPage> Handle(SearchHistory query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var repository = string.IsNullOrWhiteSpace(query.Repository) ? (RepositoryName?)null : RepositoryName.From(query.Repository);
        var text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim();
        var (found, total) = await search.SearchAsync(query.States, repository, text, (query.Page - 1) * query.PageSize, query.PageSize, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        return new HistoryPage(found.Select(j => JobViews.Summary(j, activity, now)).ToList(), total, query.Page, query.PageSize);
    }
}
