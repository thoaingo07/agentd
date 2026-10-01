using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// Take the next runnable job, prepare its worktree and return the agent request. The caller (a Host
/// worker) runs the agent in the background and reports back through <see cref="HandleAgentExit"/>.
/// A deferred job (e.g. after a usage limit) resumes its existing worktree and session.
/// </summary>
public sealed record StartNextJob(string Worker);

public sealed class StartNextJobHandler(
    IJobRepository jobs,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IWorkItemSource workItems,
    IOptions<JobOptions> options) : ICommandHandler<StartNextJob, AgentRunRequest?>
{
    public async Task<Result<AgentRunRequest?>> Handle(StartNextJob command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var job = await jobs.DequeueNextAsync(command.Worker, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return Result.Success<AgentRunRequest?>(null);
        }

        try
        {
            var repository = await repositories.GetAsync(job.Repository, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Repository '{job.Repository}' is no longer registered.");

            // A deferred job keeps its branch, worktree and session: resume instead of starting over.
            var resume = job.Session is not null && job.Worktree is not null && job.Branch is not null;
            var branch = job.Branch ?? BranchName.For(job.WorkItemId, job.Title, options.Value.BranchPrefix);
            var session = job.Session ?? ClaudeSessionId.New();

            await worktrees.EnsureCloneAsync(repository, cancellationToken).ConfigureAwait(false);
            var worktree = resume
                ? job.Worktree!.Value
                : await worktrees.CreateAsync(repository, job.WorkItemId, branch, cancellationToken).ConfigureAwait(false);

            string prompt;
            if (resume)
            {
                prompt = TaskPromptBuilder.ResumePrompt;
            }
            else
            {
                var item = await workItems.GetAsync(job.WorkItemId.Value, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Work item {job.WorkItemId} no longer exists.");
                prompt = TaskPromptBuilder.Build(item, branch, repository.BaseBranch);
            }

            var started = job.Start(worktree, branch, session);
            if (!started.IsSuccess)
            {
                return started.Error;
            }

            var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            return saved.IsSuccess
                ? new AgentRunRequest(job.Id, job.WorkItemId, worktree, session, prompt, resume)
                : saved.Error;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Fail($"Preparing the job failed: {ex.Message}");
            await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            return DomainError.Validation($"Job {job.Id} failed while preparing: {ex.Message}");
        }
    }
}
