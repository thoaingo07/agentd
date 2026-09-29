using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>
/// Push the job's branch and open (or reuse) its pull request, then complete the job.
/// Idempotent: safe to re-run after a restart while <see cref="JobState.Publishing"/>.
/// <paramref name="Draft"/> is null when resuming after a restart (a generic title is used).
/// </summary>
public sealed record PublishPullRequest(JobId JobId, PullRequestDraft? Draft = null);

public sealed class PublishPullRequestHandler(
    IJobRepository jobs,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IPullRequestService pullRequests,
    IWorkItemSource workItems) : ICommandHandler<PublishPullRequest, PullRequestRef>
{
    public async Task<Result<PullRequestRef>> Handle(PublishPullRequest command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        if (job.State != JobState.Publishing || job.Worktree is not { } worktree || job.Branch is not { } branch)
        {
            return DomainError.InvalidTransition(job.State, "publish");
        }

        try
        {
            var repository = await repositories.GetAsync(job.Repository, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Repository '{job.Repository}' is no longer registered.");

            if (!await worktrees.HasCommitsAheadAsync(repository, worktree, cancellationToken).ConfigureAwait(false))
            {
                return await FailAsync(job, "The agent finished without committing any changes.", cancellationToken).ConfigureAwait(false);
            }

            await worktrees.PushAsync(worktree, branch, cancellationToken).ConfigureAwait(false);

            var draft = command.Draft ?? job.Draft;
            var pr = await pullRequests.FindOpenAsync(repository, branch, cancellationToken).ConfigureAwait(false)
                ?? await pullRequests.CreateAsync(
                    repository,
                    branch,
                    repository.BaseBranch,
                    draft?.Title ?? $"{job.Title} (WI-{job.WorkItemId})",
                    draft?.Description ?? draft?.Summary ?? string.Empty,
                    job.WorkItemId,
                    cancellationToken).ConfigureAwait(false);

            var completed = job.Complete(new PullRequestUrl(pr.Url));
            if (!completed.IsSuccess)
            {
                return completed.Error;
            }

            var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return saved.Error;
            }

            await workItems.AddCommentAsync(job.WorkItemId.Value, $"agentd opened pull request: {pr.Url}", cancellationToken).ConfigureAwait(false);
            return pr;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await FailAsync(job, $"Publishing the pull request failed: {ex.Message}", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Result<PullRequestRef>> FailAsync(Job job, string reason, CancellationToken ct)
    {
        job.Fail(reason);
        await jobs.SaveAsync(job, ct).ConfigureAwait(false);
        return DomainError.Validation(reason);
    }
}
