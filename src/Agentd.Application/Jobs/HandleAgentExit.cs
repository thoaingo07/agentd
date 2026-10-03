using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>React to the agent process ending: finished jobs move on; anything else fails or is deferred.</summary>
public sealed record HandleAgentExit(JobId JobId, AgentRunOutcome Outcome);

public sealed class HandleAgentExitHandler(
    IJobRepository jobs,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IClock clock,
    IOptions<JobOptions> options) : ICommandHandler<HandleAgentExit, JobState>
{
    public async Task<Result<JobState>> Handle(HandleAgentExit command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        // Finished (PR opened) or cancelled while the agent was still running: now that the process is gone,
        // its worktree can be removed. (The branch and its commits stay in the clone and on the remote.)
        if (job.State is JobState.Done or JobState.Cancelled)
        {
            await RemoveWorktreeAsync(job, cancellationToken).ConfigureAwait(false);
            return job.State;
        }

        // Publishing (retrying) or any other non-running state: nothing to do here.
        if (job.State != JobState.Running)
        {
            return job.State;
        }

        // Developer replies arrived during the turn: stay Running; the scheduler starts a resume turn with them.
        if (command.Outcome is AgentRunOutcome.Exited { Summary.IsError: not true } && job.PendingMessages.Count > 0)
        {
            return job.State;
        }

        var changed = command.Outcome switch
        {
            AgentRunOutcome.UsageLimited u => job.Defer(u.ResetAt ?? clock.UtcNow.Add(options.Value.UsageLimitBackoff), "Claude usage limit reached; waiting for it to reset."),
            AgentRunOutcome.Cancelled => job.Cancel("runner"),
            AgentRunOutcome.TimedOut => job.Fail("The agent stopped producing output (idle timeout)."),
            AgentRunOutcome.Exited { Summary.IsError: true } e => job.Fail($"The agent ended with an error ({e.Summary.ErrorSubtype ?? "unknown"}) without calling finish."),
            AgentRunOutcome.Exited e => job.Fail($"The agent exited (code {e.ExitCode}) without calling finish."),
            _ => job.Fail("The agent ended unexpectedly."),
        };
        if (!changed.IsSuccess)
        {
            return changed.Error;
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? job.State : saved.Error;
    }

    private async Task RemoveWorktreeAsync(Domain.Jobs.Job job, CancellationToken ct)
    {
        if (job.Worktree is { } worktree && await repositories.GetAsync(job.Repository, ct).ConfigureAwait(false) is { } repository)
        {
            await worktrees.RemoveAsync(repository, worktree, ct).ConfigureAwait(false);
        }
    }
}
