using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;

namespace Agentd.Application.Jobs;

/// <summary>Active jobs, or (with <paramref name="IncludeRecent"/>) every job from the last 24 hours.</summary>
public sealed record GetJobStatus(bool IncludeRecent = false);

public sealed record JobStatusRow(
    long Id,
    int WorkItemId,
    string Repository,
    JobState State,
    TimeSpan Elapsed,
    int Attempt,
    string? PullRequestUrl,
    string? LastError);

public sealed class GetJobStatusHandler(IJobRepository jobs, Domain.Common.IClock clock) : IQueryHandler<GetJobStatus, IReadOnlyList<JobStatusRow>>
{
    private static readonly JobState[] s_active = [JobState.Queued, JobState.Preparing, JobState.Running, JobState.WaitingForHuman, JobState.Publishing, JobState.InReview];

    public async Task<IReadOnlyList<JobStatusRow>> Handle(GetJobStatus query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var list = query.IncludeRecent
            ? await jobs.ListRecentAsync(TimeSpan.FromHours(24), cancellationToken).ConfigureAwait(false)
            : await jobs.ListByStateAsync(s_active, cancellationToken).ConfigureAwait(false);

        var now = clock.UtcNow;
        return list
            .OrderBy(j => j.CreatedAt)
            .Select(j => new JobStatusRow(
                j.Id.Value,
                j.WorkItemId.Value,
                j.Repository.Value,
                j.State,
                (j.State.IsTerminal() ? j.UpdatedAt : now) - j.CreatedAt,
                j.Attempt,
                j.PullRequest?.Value.ToString(),
                j.LastError))
            .ToList();
    }
}
