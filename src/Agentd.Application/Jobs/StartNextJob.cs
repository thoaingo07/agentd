using Agentd.Application.Abstractions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
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
    MessagingService messaging,
    IOutbox outbox,
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
            // Resuming reuses the worktree, or recreates it at the same path from the kept branch (e.g. after a cancel
            // removed it), so the Claude session, which is tied to that path, can continue.
            var worktree = await worktrees.CreateAsync(repository, job.WorkItemId, branch, cancellationToken).ConfigureAwait(false);

            WorkItemDetails? item = null;
            string prompt;
            if (resume)
            {
                prompt = TaskPromptBuilder.ResumePrompt;
            }
            else
            {
                item = await workItems.GetAsync(job.WorkItemId.Value, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"Work item {job.WorkItemId} no longer exists.");
                prompt = TaskPromptBuilder.Build(item, branch, repository.BaseBranch,
                    options.Value.RequirePlanApproval && !item.Tags.Contains(options.Value.AutoTag, StringComparer.OrdinalIgnoreCase));
            }

            // Plan approval (on by default): the first turns are read-only until the developer approves.
            var gate = item is not null && options.Value.RequirePlanApproval
                && !item.Tags.Contains(options.Value.AutoTag, StringComparer.OrdinalIgnoreCase);
            var started = job.Start(worktree, branch, session, gate);
            if (!started.IsSuccess)
            {
                return started.Error;
            }

            var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return saved.Error;
            }

            // A first start opens the job's conversations; a resumed job already has them.
            if (item is not null)
            {
                await messaging.OpenConversationsAsync(job, item, cancellationToken).ConfigureAwait(false);
                await outbox.TryEnqueueAsync(job.Id, MessageCatalog.Started(job, item, repository.BaseBranch), cancellationToken).ConfigureAwait(false);
            }

            return new AgentRunRequest(job.Id, job.WorkItemId, worktree, session, prompt, resume, ReadOnly: job.PlanStatus == PlanStatus.Pending, Step: JobSteps.Of(job));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Fail($"Preparing the job failed: {ex.Message}");
            await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            return DomainError.Validation($"Job {job.Id} failed while preparing: {ex.Message}");
        }
    }
}
