using Agentd.Application.Abstractions;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Reviews;

/// <summary>
/// What to review (docs/architect/review-sessions.md §3): a PR, a branch (against its base, or <see cref="Base"/>),
/// or two commits (<see cref="Base"/> and <see cref="Head"/>). Exactly one of them.
/// </summary>
public sealed record StartReview(string Repository, int? PullRequestId = null, string? Branch = null, string? Base = null, string? Head = null);

/// <summary>A session with its comments and questions, as the page shows it.</summary>
public sealed record ReviewSessionView(ReviewSession Session, IReadOnlyList<ReviewComment> Comments, IReadOnlyList<ReviewAsk> Asks);

/// <summary>
/// Review sessions on the server: the change is fetched with agentd's credentials into its clone and its commits are
/// pinned, so the diff, the findings and the comments always agree. People keep, drop or edit findings and comment.
/// </summary>
public sealed class ReviewSessionService(
    IReviewSessionStore store,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IPullRequestService pullRequests,
    IOptions<JobOptions>? jobs = null)
{
    /// <summary>The same cap as a job's Diff tab: past it, only the file list.</summary>
    public const int MaxDiffBytes = 2 * 1024 * 1024;

    public const int MaxText = 4000;

    public async Task<Result<ReviewSession>> StartAsync(StartReview request, string createdBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var targets = (request.PullRequestId is not null ? 1 : 0) + (!string.IsNullOrWhiteSpace(request.Branch) ? 1 : 0)
            + (request.PullRequestId is null && string.IsNullOrWhiteSpace(request.Branch) && !string.IsNullOrWhiteSpace(request.Head) ? 1 : 0);
        if (targets != 1 || (request.Head is not null && string.IsNullOrWhiteSpace(request.Base)))
        {
            return DomainError.Validation("Say what to review: a PR, a branch, or two commits (base and head).");
        }

        if (RepositoryName.Create(request.Repository ?? string.Empty) is not { IsSuccess: true } name
            || await repositories.GetAsync(name.Value, cancellationToken).ConfigureAwait(false) is not { } repo)
        {
            return DomainError.NotFound($"Repository '{request.Repository}'");
        }

        await worktrees.EnsureCloneAsync(repo, cancellationToken).ConfigureAwait(false);   // fetches: the latest branches and PR heads
        var pinned = await PinAsync(repo, request, cancellationToken).ConfigureAwait(false);
        if (!pinned.IsSuccess)
        {
            return pinned.Error;
        }

        var (target, headRef, baseRef, baseCommit, headCommit) = pinned.Value;
        var defaults = jobs?.Value.Steps.TryGetValue(JobSteps.Review, out var step) == true ? step : null;
        var id = await store.InsertAsync(repo.Name.Value, target, request.PullRequestId, headRef, baseRef, createdBy,
            Blank(defaults?.Model), Blank(defaults?.Effort)?.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        await store.PinAsync(id, baseCommit, headCommit, null, cancellationToken).ConfigureAwait(false);
        await store.SetStatusAsync(id, ReviewSessionStatus.Ready, null, null, cancellationToken).ConfigureAwait(false);
        return (await store.GetAsync(id, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<ReviewSessionView?> GetAsync(long id, CancellationToken cancellationToken) =>
        await store.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } session
            ? new ReviewSessionView(session,
                await store.ListCommentsAsync(id, cancellationToken).ConfigureAwait(false),
                await store.ListAsksAsync(id, cancellationToken).ConfigureAwait(false))
            : null;

    public Task<IReadOnlyList<ReviewSession>> ListAsync(string createdBy, CancellationToken cancellationToken) =>
        store.ListAsync(createdBy, 50, cancellationToken);

    /// <summary>The pinned change, recomputed from the clone (never stored).</summary>
    public async Task<Result<BranchDiff>> DiffAsync(long id, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { BaseCommit: { } baseCommit, HeadCommit: { } headCommit } session)
        {
            return DomainError.NotFound($"Review {id}");
        }

        if (RepositoryName.Create(session.Repository) is not { IsSuccess: true } name
            || await repositories.GetAsync(name.Value, cancellationToken).ConfigureAwait(false) is not { } repo)
        {
            return DomainError.NotFound($"Repository '{session.Repository}'");
        }

        return await worktrees.DiffCommitsAsync(repo, baseCommit, headCommit, MaxDiffBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="id">The session.</param>
    /// <param name="number">The finding's 1-based number, as the page shows it.</param>
    /// <param name="decision">kept, dropped or edited.</param>
    /// <param name="edited">The person's text for an edited finding.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    public async Task<Result<Unit>> DecideAsync(long id, int number, string decision, string? edited, CancellationToken cancellationToken)
    {
        if (!FindingDecisions.IsValid(decision))
        {
            return DomainError.Validation("A decision is kept, dropped or edited.");
        }

        if (decision == FindingDecisions.Edited && (string.IsNullOrWhiteSpace(edited) || edited.Length > MaxText))
        {
            return DomainError.Validation($"An edited finding needs its new text (up to {MaxText} characters).");
        }

        var open = await OpenAsync(id, cancellationToken).ConfigureAwait(false);
        if (!open.IsSuccess)
        {
            return open.Error;
        }

        return await store.DecideAsync(id, number - 1, decision, decision == FindingDecisions.Edited ? edited!.Trim() : null, cancellationToken).ConfigureAwait(false)
            ? Unit.Value
            : DomainError.NotFound($"Finding {number} of review {id}");
    }

    public async Task<Result<ReviewComment>> AddCommentAsync(long id, string? file, int? line, int? endLine, string text, string author, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxText)
        {
            return DomainError.Validation($"Write the comment (up to {MaxText} characters).");
        }

        if (line is < 1 || (endLine is not null && (line is null || endLine < line)) || (line is not null && string.IsNullOrWhiteSpace(file)))
        {
            return DomainError.Validation("A comment's lines start at 1, end at or after they start, and belong to a file.");
        }

        var open = await OpenAsync(id, cancellationToken).ConfigureAwait(false);
        if (!open.IsSuccess)
        {
            return open.Error;
        }

        var commentId = await store.AddCommentAsync(id, Blank(file), line, endLine, text.Trim(), author, cancellationToken).ConfigureAwait(false);
        return (await store.ListCommentsAsync(id, cancellationToken).ConfigureAwait(false)).Single(c => c.Id == commentId);
    }

    /// <summary>Only its author removes a comment.</summary>
    public async Task<Result<Unit>> DeleteCommentAsync(long id, long commentId, string author, CancellationToken cancellationToken) =>
        await store.DeleteCommentAsync(id, commentId, author, cancellationToken).ConfigureAwait(false)
            ? Unit.Value
            : DomainError.NotFound($"Your comment {commentId} on review {id}");

    /// <summary>The base and head commits for <paramref name="request"/>, from the freshly fetched clone.</summary>
    private async Task<Result<(string Target, string? HeadRef, string? BaseRef, string BaseCommit, string HeadCommit)>> PinAsync(Repository repo, StartReview request, CancellationToken ct)
    {
        if (request.PullRequestId is { } pr)
        {
            if (await pullRequests.GetAsync(repo, pr, ct).ConfigureAwait(false) is not { } details)
            {
                return DomainError.NotFound($"PR !{pr} in {repo.Name}");
            }

            var prHead = await worktrees.ResolveCommitAsync(repo, details.SourceCommit, ct).ConfigureAwait(false)
                ?? await worktrees.ResolveCommitAsync(repo, details.SourceBranch, ct).ConfigureAwait(false);
            return await AgainstAsync(repo, ReviewTargets.PullRequest, details.SourceBranch, details.TargetBranch, prHead, ct).ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(request.Branch))
        {
            var branch = request.Branch.Trim();
            var head = await worktrees.ResolveCommitAsync(repo, branch, ct).ConfigureAwait(false);
            return head is null
                ? DomainError.NotFound($"Branch '{branch}' in {repo.Name} (is it pushed?)")
                : await AgainstAsync(repo, ReviewTargets.Branch, branch, Blank(request.Base) ?? repo.BaseBranch, head, ct).ConfigureAwait(false);
        }

        var from = await worktrees.ResolveCommitAsync(repo, request.Base!, ct).ConfigureAwait(false);
        var to = await worktrees.ResolveCommitAsync(repo, request.Head!, ct).ConfigureAwait(false);
        return from is null || to is null
            ? DomainError.NotFound($"Commit '{(from is null ? request.Base : request.Head)}' in {repo.Name}")
            : (ReviewTargets.Range, request.Head!.Trim(), request.Base!.Trim(), from, to);
    }

    /// <summary>A branch is compared with where it left its base, so later base commits aren't part of its change.</summary>
    private async Task<Result<(string, string?, string?, string, string)>> AgainstAsync(Repository repo, string target, string headRef, string baseRef, string? head, CancellationToken ct)
    {
        if (head is null)
        {
            return DomainError.NotFound($"The head of {headRef} in {repo.Name}");
        }

        var baseTip = await worktrees.ResolveCommitAsync(repo, baseRef, ct).ConfigureAwait(false);
        var mergeBase = baseTip is null ? null : await worktrees.MergeBaseAsync(repo, baseTip, head, ct).ConfigureAwait(false);
        return mergeBase is null
            ? DomainError.Validation($"{headRef} and {baseRef} share no history in {repo.Name}.")
            : (target, headRef, baseRef, mergeBase, head);
    }

    /// <summary>Decisions and comments only while the session is open.</summary>
    private async Task<Result<ReviewSession>> OpenAsync(long id, CancellationToken ct) =>
        await store.GetAsync(id, ct).ConfigureAwait(false) switch
        {
            null => DomainError.NotFound($"Review {id}"),
            { Status: ReviewSessionStatus.Sent or ReviewSessionStatus.Closed } done => DomainError.Conflict($"Review {id} is {done.Status.ToLowerInvariant()}."),
            var session => session,
        };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
