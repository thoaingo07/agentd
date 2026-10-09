using System.Globalization;
using Agentd.Application.Abstractions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// Push the job's branch and open (or reuse) its pull request, then complete the job.
/// Idempotent: safe to re-run after a restart or a failed attempt while <see cref="JobState.Publishing"/>.
/// <paramref name="Draft"/> is null when retrying (the stored draft is used).
/// </summary>
public sealed record PublishPullRequest(JobId JobId, PullRequestDraft? Draft = null);

public sealed class PublishPullRequestHandler(
    IJobRepository jobs,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IPullRequestService pullRequests,
    IWorkItemSource workItems,
    IOutbox outbox,
    JobActivity activity,
    IClock clock,
    IOptions<JobOptions> options,
    ICommandHandler<StartHandoff, Unit>? handoff = null,
    AzureDevOps.AdoOnBehalf? onBehalf = null,
    AzureDevOps.AdoActor? actor = null) : ICommandHandler<PublishPullRequest, PullRequestRef>
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

        var repository = await repositories.GetAsync(job.Repository, cancellationToken).ConfigureAwait(false);
        if (repository is null)
        {
            return await FailAsync(job, $"Repository '{job.Repository}' is no longer registered.", cancellationToken).ConfigureAwait(false);
        }

        // Everything below for the job (the PR, its replies, the work item's comments) goes under the work item's Assigned To
        // when they've connected their Azure DevOps; otherwise agentd's own, said once in the thread.
        var assignee = onBehalf is null || await workItems.GetAsync(job.WorkItemId.Value, cancellationToken).ConfigureAwait(false) is not { } item
            ? null
            : await onBehalf.ResolveAsync(item.AssignedToId, item.AssignedTo, job.Id, cancellationToken).ConfigureAwait(false);
        using var asAssignee = actor?.Begin(assignee);
        try
        {
            // The job's PR may have been merged or abandoned while the agent was still working (a fix round, a question):
            // never open a second PR for the same work item. Merged ends the job; abandoned cancels it and says why.
            if (job.PullRequest is { } previous && ReviewPullRequestsHandler.PullRequestId(previous.Value) is { } previousId)
            {
                switch (await pullRequests.GetStatusAsync(repository, previousId, cancellationToken).ConfigureAwait(false))
                {
                    case PullRequestStatus.Completed:
                        // Merged like any other PR, so the hand-off follows, as it does from the review loop.
                        var handoffNext = options.Value.Handoff && handoff is not null && job.Handoff == HandoffStatus.None;
                        var ended = await EndWithoutPublishAsync(job, job.MergedBeforePublish(), MessageCatalog.MergedBeforePublish(previous.Value, branch.Value, handoffNext), cancellationToken).ConfigureAwait(false);
                        if (handoffNext && ended.Error?.Code == NotPublished)
                        {
                            await handoff!.Handle(new StartHandoff(job.Id), cancellationToken).ConfigureAwait(false);
                        }

                        return ended;
                    case PullRequestStatus.Abandoned:
                        return await EndWithoutPublishAsync(job, job.Cancel("PR abandoned"), MessageCatalog.AbandonedBeforePublish(previous.Value), cancellationToken).ConfigureAwait(false);
                }
            }

            if (!await worktrees.HasCommitsAheadAsync(repository, worktree, cancellationToken).ConfigureAwait(false))
            {
                await workItems.AddCommentAsync(job.WorkItemId.Value, "agentd: the agent finished without committing any changes, so no pull request was opened.", cancellationToken).ConfigureAwait(false);
                return await FailAsync(job, "The agent finished without committing any changes.", cancellationToken).ConfigureAwait(false);
            }

            await worktrees.PushAsync(worktree, branch, cancellationToken).ConfigureAwait(false);
            await outbox.TryEnqueueAsync(job.Id, MessageCatalog.Pushed(branch.Value), cancellationToken).ConfigureAwait(false);

            var pr = await pullRequests.FindOpenAsync(repository, branch, cancellationToken).ConfigureAwait(false)
                ?? await pullRequests.CreateAsync(
                    repository,
                    branch,
                    repository.BaseBranch,
                    Title(job, command.Draft ?? job.Draft),
                    Description(job, command.Draft ?? job.Draft),
                    job.WorkItemId,
                    cancellationToken).ConfigureAwait(false);

            var firstPublish = job.PullRequest is null;
            var roundThreads = job.TakeRoundThreads();
            var completed = options.Value.ReviewLoop ? job.OpenForReview(new PullRequestUrl(pr.Url)) : job.Complete(new PullRequestUrl(pr.Url));
            if (!completed.IsSuccess)
            {
                return completed.Error;
            }

            var saved = await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                return saved.Error;
            }

            await CommentOnceAsync(job, pr, cancellationToken).ConfigureAwait(false);
            foreach (var thread in roundThreads)
            {
                try
                {
                    await pullRequests.ReplyAsync(repository, pr.Id, thread, $"Addressed in the latest push (fix round {job.FixRounds}). Please review.", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missing reply must not fail the publish; the developer still sees the push in the thread.
                }
            }

            if (firstPublish && job.Estimate is { } estimate)
            {
                await outbox.TryEnqueueAsync(job.Id, MessageCatalog.ActualVsEstimate(estimate, clock.UtcNow, activity.Get(job.Id).Usage?.FiveHour), cancellationToken).ConfigureAwait(false);
            }

            return pr;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Transient push/API failures: stay in Publishing and retry with exponential backoff.
            var o = options.Value;
            var delay = o.PublishRetryDelay * Math.Pow(2, job.PublishAttempts);
            job.PublishFailed($"Publishing failed: {ex.Message}", o.PublishMaxAttempts, clock.UtcNow + delay);
            await jobs.SaveAsync(job, cancellationToken).ConfigureAwait(false);
            return DomainError.Validation(job.LastError ?? ex.Message);
        }
    }

    internal static string Title(Job job, PullRequestDraft? draft)
    {
        var title = draft?.Title ?? job.Title;
        var suffix = string.Create(CultureInfo.InvariantCulture, $"(WI-{job.WorkItemId})");
        return title.Contains(suffix, StringComparison.OrdinalIgnoreCase) ? title : $"{title} {suffix}";
    }

    internal static string Description(Job job, PullRequestDraft? draft)
    {
        var body = draft?.Description is { Length: > 0 } d ? d : draft?.Summary ?? string.Empty;
        return $"{body}\n\n---\nWork item: #{job.WorkItemId} · agentd job {job.Id} · Created by agentd";
    }

    private async Task CommentOnceAsync(Job job, PullRequestRef pr, CancellationToken ct)
    {
        // Idempotent: a retried publish must not comment twice.
        var item = await workItems.GetAsync(job.WorkItemId.Value, ct).ConfigureAwait(false);
        var url = pr.Url.ToString();
        if (item is null || !item.Comments.Any(c => c.Text.Contains(url, StringComparison.Ordinal)))
        {
            await workItems.AddCommentAsync(job.WorkItemId.Value, $"agentd opened pull request !{pr.Id}: {url}", ct).ConfigureAwait(false);
        }
    }

    /// <summary>The error code of a finish that ended the job without publishing (merged or abandoned meanwhile).</summary>
    public const string NotPublished = "not_published";

    private async Task<Result<PullRequestRef>> EndWithoutPublishAsync(Job job, Result ended, OutboundMessage message, CancellationToken ct)
    {
        if (!ended.IsSuccess)
        {
            return ended.Error;
        }

        var saved = await jobs.SaveAsync(job, ct).ConfigureAwait(false);
        if (!saved.IsSuccess)
        {
            return saved.Error;
        }

        await outbox.TryEnqueueAsync(job.Id, message, ct).ConfigureAwait(false);
        return new DomainError(NotPublished, message.Markdown);
    }

    private async Task<Result<PullRequestRef>> FailAsync(Job job, string reason, CancellationToken ct)
    {
        job.Fail(reason);
        await jobs.SaveAsync(job, ct).ConfigureAwait(false);
        return DomainError.Validation(reason);
    }
}
