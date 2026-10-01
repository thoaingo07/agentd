using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;

namespace Agentd.Application.Jobs;

/// <summary>
/// After a restart: prune stale worktrees, requeue jobs interrupted while preparing, resume orphaned
/// running jobs in their existing Claude session, and publish jobs that finished but were not published.
/// </summary>
public sealed record RecoverJobsOnStartup;

/// <summary>Agent runs to resume, plus counts of the other recovery actions.</summary>
public sealed record RecoveryPlan(IReadOnlyList<AgentRunRequest> Resume, int RepublishedCount, int RequeuedCount, int FailedCount);

public sealed class RecoverJobsOnStartupHandler(
    IJobRepository jobs,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IAgentRunner runner,
    IClock clock,
    ICommandHandler<PublishPullRequest, PullRequestRef> publish) : ICommandHandler<RecoverJobsOnStartup, RecoveryPlan>
{
    public async Task<Result<RecoveryPlan>> Handle(RecoverJobsOnStartup command, CancellationToken cancellationToken)
    {
        await PruneWorktreesAsync(cancellationToken).ConfigureAwait(false);

        var active = await jobs.ListByStateAsync([JobState.Preparing, JobState.Running, JobState.Publishing], cancellationToken).ConfigureAwait(false);
        var resume = new List<AgentRunRequest>();
        int republished = 0, requeued = 0, failed = 0;

        foreach (var job in active)
        {
            switch (job.State)
            {
                case JobState.Running when runner.IsRunning(job.Id):
                    break;

                // The process died with agentd. The runner issues a fresh MCP token (and mcp.json) for the resume run.
                case JobState.Running when job is { Worktree: { } wt, Session: { } session }:
                    if (job.MarkRecovered().IsSuccess && (await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false)).IsSuccess)
                    {
                        resume.Add(new AgentRunRequest(job.Id, job.WorkItemId, wt, session, TaskPromptBuilder.ResumePrompt, Resume: true));
                    }

                    break;

                case JobState.Running:
                    job.Fail("agentd restarted and the job has no worktree or session to resume.");
                    await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
                    failed++;
                    break;

                // A scheduled publish retry is left to the scheduler until it is due.
                case JobState.Publishing when job.NotBefore is null || job.NotBefore <= clock.UtcNow:
                    await publish.Handle(new PublishPullRequest(job.Id), cancellationToken).ConfigureAwait(false);
                    republished++;
                    break;

                // Preparation is idempotent (the worktree is reused), so start it again.
                case JobState.Preparing:
                    job.Requeue("agentd restarted while the job was being prepared.");
                    await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
                    requeued++;
                    break;
            }
        }

        return new RecoveryPlan(resume, republished, requeued, failed);
    }

    private async Task PruneWorktreesAsync(CancellationToken cancellationToken)
    {
        foreach (var repository in await repositories.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await worktrees.PruneAsync(repository, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Best effort: a repository that cannot be pruned (e.g. not cloned yet) must not block recovery.
            }
        }
    }
}
