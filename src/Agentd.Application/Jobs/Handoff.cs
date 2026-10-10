using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>Start the knowledge hand-off for a job whose PR was merged (automatic, or <c>!handoff</c>).</summary>
public sealed record StartHandoff(JobId JobId);

/// <summary>
/// Recreates the work item's worktree (same path, so the Claude session resumes) on <c>ai/&lt;id&gt;-knowledge</c>
/// from the latest base branch, and queues a hand-off turn.
/// </summary>
public sealed class StartHandoffHandler(
    IJobRepository jobs,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IOptions<JobOptions> options,
    IWorkItemSource? workItems = null,
    AzureDevOps.AdoOnBehalf? onBehalf = null) : ICommandHandler<StartHandoff, Unit>
{
    public async Task<Result<Unit>> Handle(StartHandoff command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        if (job.State is not (JobState.InReview or JobState.Done) || job.PullRequest is null || job.Handoff != HandoffStatus.None)
        {
            return DomainError.Validation(job.Handoff != HandoffStatus.None ? "The hand-off already ran for this job." : "The hand-off starts after the PR is merged.");
        }

        if (await repositories.GetAsync(job.Repository, cancellationToken).ConfigureAwait(false) is not { } repository)
        {
            return DomainError.NotFound($"Repository '{job.Repository}'");
        }

        var branch = BranchName.For(job.WorkItemId, "knowledge", options.Value.BranchPrefix);
        var worktree = await worktrees.RecreateAsync(repository, job.WorkItemId, branch, cancellationToken).ConfigureAwait(false);
        if (onBehalf is not null && workItems is not null)
        {
            var item = await workItems.GetAsync(job.WorkItemId.Value, cancellationToken).ConfigureAwait(false);
            var author = await onBehalf.CommitAuthorAsync(item?.AssignedToId, item?.AssignedTo, job.Id, cancellationToken).ConfigureAwait(false);
            await worktrees.SetCommitAuthorAsync(worktree, author, cancellationToken).ConfigureAwait(false);
        }

        var started = job.StartHandoff(branch, worktree);
        if (!started.IsSuccess)
        {
            return started.Error;
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }
}

/// <summary>The agent proposes the knowledge changes (MCP <c>propose_knowledge</c>); the developer agrees, changes or declines.</summary>
public sealed record ProposeKnowledge(JobId JobId, string Proposal);

public sealed class ProposeKnowledgeHandler(IJobRepository jobs) : ICommandHandler<ProposeKnowledge, Unit>
{
    public const string SyncLabel = "✅ Sync these changes";
    public const string ChangeLabel = "✏️ Change something";
    public const string SkipLabel = "🚫 Don't sync";
    public const int MaxLength = 1800;

    private static readonly HashSet<string> s_declines = new(StringComparer.OrdinalIgnoreCase)
    {
        "3", SkipLabel, "don't sync", "dont sync", "do not sync", "no sync", "no", "skip", "nothing", "none",
    };

    public async Task<Result<Unit>> Handle(ProposeKnowledge command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.Proposal) || command.Proposal.Length > MaxLength)
        {
            return DomainError.Validation($"The proposal must be 1–{MaxLength} characters.");
        }

        var job = await jobs.GetAsync(command.JobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return DomainError.NotFound($"Job {command.JobId}");
        }

        if (job.Handoff != HandoffStatus.Proposing)
        {
            return DomainError.Validation("Propose knowledge only during the hand-off.");
        }

        var asked = job.AskDeveloper(
            $"🎓 **Knowledge and learnings to sync**\n\n{command.Proposal.Trim()}\n\nShall I open a pull request with these changes?",
            [SyncLabel, ChangeLabel, SkipLabel]);
        if (!asked.IsSuccess)
        {
            return asked.Error;
        }

        var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
        return saved.IsSuccess ? Unit.Value : saved.Error;
    }

    /// <summary>Whether a reply declines the sync ("3", the button, "don't sync", "skip", "no", …).</summary>
    public static bool IsDecline(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var text = reply.Trim().TrimEnd('.', '!');
        return s_declines.Contains(text) || text.StartsWith("don't sync", StringComparison.OrdinalIgnoreCase) || text.StartsWith("no sync", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a reply agrees with the proposal ("1", the button, "sync", plus the plan approvals).</summary>
    public static bool IsAgreement(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        var text = reply.Trim().TrimEnd('.', '!');
        return string.Equals(text, SyncLabel, StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("sync", StringComparison.OrdinalIgnoreCase)
            || SubmitPlanHandler.IsApproval(text);
    }
}
