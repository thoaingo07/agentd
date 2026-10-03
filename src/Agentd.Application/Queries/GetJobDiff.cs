using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Queries;

/// <summary>A job's changes against its base branch, for the session view's diff tab.</summary>
public sealed record GetJobDiff(JobId JobId);

public sealed class GetJobDiffHandler(IJobRepository jobs, IRepositoryRegistry repositories, IWorktreeManager worktrees)
    : IQueryHandler<GetJobDiff, Result<BranchDiff>>
{
    /// <summary>Bigger diffs return the file list only.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    public async Task<Result<BranchDiff>> Handle(GetJobDiff query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (await jobs.GetAsync(query.JobId, cancellationToken).ConfigureAwait(false) is not { } job)
        {
            return DomainError.NotFound($"Job {query.JobId}");
        }

        if (job.Branch is not { } branch
            || await repositories.GetAsync(job.Repository, cancellationToken).ConfigureAwait(false) is not { } repository)
        {
            return DomainError.NotFound($"A branch for job {query.JobId}");
        }

        var diff = await worktrees.DiffAsync(repository, branch, job.Worktree, MaxBytes, cancellationToken).ConfigureAwait(false);
        return diff is null ? DomainError.NotFound($"Branch {branch}") : diff;
    }
}
