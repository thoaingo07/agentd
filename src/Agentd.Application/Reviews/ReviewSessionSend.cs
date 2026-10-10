using Agentd.Application.AzureDevOps;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Reviews;

/// <summary>Where a review was sent and what came of it.</summary>
/// <param name="Destination"><c>pr</c> or <c>text</c>.</param>
/// <param name="Text">For <c>text</c>: what to copy.</param>
/// <param name="Url">For <c>pr</c>: the pull request.</param>
/// <param name="Posted">For <c>pr</c>: threads opened (findings and comments, plus the main message).</param>
/// <param name="AsPerson">For <c>pr</c>: posted under the reviewer's own Azure DevOps name (else agentd's).</param>
public sealed record ReviewSent(string Destination, string? Text, Uri? Url, int Posted, bool AsPerson);

/// <summary>
/// Send on the server (docs/architect/review-sessions.md §4): post a PR review's kept and edited findings and the comments to
/// the PR, as threads plus one main message, under the name of whoever started it when they've connected their Azure
/// DevOps (docs/architect/ado-user-delegation.md); or hand back the text to copy.
/// </summary>
public sealed class ReviewSessionSend(
    IReviewSessionStore store,
    IRepositoryRegistry repositories,
    IPullRequestService pullRequests,
    IAdoUserConnections? connections = null,
    AdoUserTokens? tokens = null,
    AdoActor? actor = null)
{
    public const string ToPullRequest = "pr";
    public const string AsText = "text";

    public async Task<Result<ReviewSent>> SendAsync(long id, string destination, CancellationToken cancellationToken)
    {
        if (await store.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } session)
        {
            return DomainError.NotFound($"Review {id}");
        }

        if (session.Status is not ReviewSessionStatus.Ready)
        {
            return DomainError.Conflict(session.Status == ReviewSessionStatus.Reviewing ? $"Review {id} is still reviewing." : $"Review {id} is {session.Status.ToLowerInvariant()}.");
        }

        var comments = await store.ListCommentsAsync(id, cancellationToken).ConfigureAwait(false);
        switch (destination)
        {
            case AsText:
                await store.SetStatusAsync(id, ReviewSessionStatus.Sent, null, AsText, cancellationToken).ConfigureAwait(false);
                return new ReviewSent(AsText, ReviewFeedback.Render($"{session.BaseRef} ({session.BaseCommit?[..Math.Min(7, session.BaseCommit.Length)]})", session.Summary, session.Findings, comments),
                    null, 0, false);
            case ToPullRequest when session is { Target: ReviewTargets.PullRequest, PullRequestId: { } pr }:
                return await PostAsync(session, pr, comments, cancellationToken).ConfigureAwait(false);
            case ToPullRequest:
                return DomainError.Validation("Only a pull request's review can be posted to a PR.");
            default:
                return DomainError.Validation("Send it to `pr` or as `text`.");
        }
    }

    private async Task<Result<ReviewSent>> PostAsync(ReviewSession session, int pr, IReadOnlyList<ReviewComment> comments, CancellationToken ct)
    {
        if (RepositoryName.Create(session.Repository) is not { IsSuccess: true } name || await repositories.GetAsync(name.Value, ct).ConfigureAwait(false) is not { } repo)
        {
            return DomainError.NotFound($"Repository '{session.Repository}'");
        }

        var person = await PersonAsync(session.CreatedBy, ct).ConfigureAwait(false);
        var kept = ReviewFeedback.Kept(session.Findings);
        var posted = new List<ReviewFinding>();
        var threads = 0;
        using (actor?.Begin(person))
        {
            try
            {
                foreach (var f in kept)
                {
                    var thread = await pullRequests.CreateThreadAsync(repo, pr, ReviewFindings.ThreadText(f), f.File, f.Line, ct).ConfigureAwait(false);
                    posted.Add(f with { Thread = thread, Status = ReviewFindings.Open });
                    threads++;
                }

                foreach (var c in comments)
                {
                    await pullRequests.CreateThreadAsync(repo, pr, $"💬 {c.Text.Trim()}", c.File, c.Line, ct).ConfigureAwait(false);
                    threads++;
                }

                await pullRequests.CreateThreadAsync(repo, pr, ReviewFindings.MainMessage(new ReviewResult(session.Summary ?? string.Empty, posted), session.HeadCommit, rechecks: false), null, null, ct)
                    .ConfigureAwait(false);
                threads++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // What's on the PR stays; the session stays Ready so it can be sent again (or as text).
                return DomainError.Validation($"Posting stopped after {threads} thread(s): {ex.Message}");
            }
        }

        await store.SetStatusAsync(session.Id, ReviewSessionStatus.Sent, null, $"{ToPullRequest}:{pr}", ct).ConfigureAwait(false);
        var url = (await pullRequests.GetAsync(repo, pr, ct).ConfigureAwait(false))?.Url;
        return new ReviewSent(ToPullRequest, null, url, threads, person is not null);
    }

    /// <summary>The reviewer's own Azure DevOps identity when they connected it from this web login and it still works.</summary>
    private async Task<Guid?> PersonAsync(string webLogin, CancellationToken ct)
    {
        if (connections is null || tokens is null || actor is null)
        {
            return null;
        }

        foreach (var c in await connections.ListByWebLoginAsync(webLogin, ct).ConfigureAwait(false))
        {
            if (!c.Failed && await tokens.GetAccessTokenAsync(c.IdentityId, forceRefresh: false, ct).ConfigureAwait(false) is not null)
            {
                return c.IdentityId;
            }
        }

        return null;
    }
}
