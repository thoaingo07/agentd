using System.Security.Claims;
using Agentd.Application.Reviews;
using Agentd.Bff.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Agentd.Bff.Endpoints;

/// <summary><c>/api/reviews</c>: review sessions on the server (docs/architect/review-sessions.md §6).</summary>
public static class ReviewSessionEndpoints
{
    public static RouteGroupBuilder MapReviewSessions(this RouteGroupBuilder api)
    {
        var reviews = api.MapGroup("/reviews");

        reviews.MapPost("/", async (StartReviewRequest? body, ClaimsPrincipal user, [FromServices] ReviewSessionService service, CancellationToken ct) =>
            (await service.StartAsync(new StartReview(body?.Repo ?? string.Empty, body?.PullRequestId, body?.Branch, body?.Base, body?.Head), AdoConnectEndpoints.Login(user), ct).ConfigureAwait(false))
                .ToHttpResult(s => TypedResults.Created($"/api/reviews/{s.Id}", ReviewSessionVm.From(s))))
            .WithName("StartReview").Produces<ReviewSessionVm>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        reviews.MapGet("/", async (ClaimsPrincipal user, [FromServices] ReviewSessionService service, CancellationToken ct) =>
            TypedResults.Ok((await service.ListAsync(AdoConnectEndpoints.Login(user), ct).ConfigureAwait(false)).Select(ReviewSessionVm.From).ToList()))
            .WithName("ListReviews");

        reviews.MapGet("/pull-requests", async ([FromServices] OpenPullRequests open, CancellationToken ct) =>
            TypedResults.Ok(OpenPullRequestsVm.From(await open.ListAsync(ct).ConfigureAwait(false))))
            .WithName("ListOpenPullRequests");

        reviews.MapGet("/{id:long}", async Task<IResult> (long id, [FromServices] ReviewSessionService service, CancellationToken ct) =>
            await service.GetAsync(id, ct).ConfigureAwait(false) is { } view ? TypedResults.Ok(ReviewSessionDetailVm.From(view)) : TypedResults.NotFound())
            .WithName("GetReview").Produces<ReviewSessionDetailVm>().Produces(StatusCodes.Status404NotFound);

        reviews.MapGet("/{id:long}/diff", async (long id, [FromServices] ReviewSessionService service, CancellationToken ct) =>
            (await service.DiffAsync(id, ct).ConfigureAwait(false)).ToHttpResult(d => TypedResults.Ok(new ReviewDiffVm(d.BaseRef, d.HeadRef, d.Files, d.UnifiedDiff, d.Truncated))))
            .WithName("GetReviewDiff").Produces<ReviewDiffVm>().ProducesProblem(StatusCodes.Status404NotFound);

        reviews.MapPut("/{id:long}/findings/{number:int}", async (long id, int number, DecisionRequest? body, [FromServices] ReviewSessionService service, CancellationToken ct) =>
            (await service.DecideAsync(id, number, body?.Decision ?? string.Empty, body?.Text, ct).ConfigureAwait(false)).ToHttpResult(_ => TypedResults.NoContent()))
            .WithName("DecideReviewFinding").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        reviews.MapPost("/{id:long}/comments", async (long id, CommentRequest? body, ClaimsPrincipal user, [FromServices] ReviewSessionService service, CancellationToken ct) =>
            (await service.AddCommentAsync(id, body?.File, body?.Line, body?.EndLine, body?.Text ?? string.Empty, AdoConnectEndpoints.Login(user), ct).ConfigureAwait(false))
                .ToHttpResult(c => TypedResults.Created($"/api/reviews/{id}/comments/{c.Id}", ReviewCommentVm.From(c))))
            .WithName("AddReviewComment").Produces<ReviewCommentVm>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        reviews.MapDelete("/{id:long}/comments/{commentId:long}", async (long id, long commentId, ClaimsPrincipal user, [FromServices] ReviewSessionService service, CancellationToken ct) =>
            (await service.DeleteCommentAsync(id, commentId, AdoConnectEndpoints.Login(user), ct).ConfigureAwait(false)).ToHttpResult(_ => TypedResults.NoContent()))
            .WithName("DeleteReviewComment").Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);

        reviews.MapPost("/{id:long}/asks", async (long id, CommentRequest? body, ClaimsPrincipal user, [FromServices] ReviewSessionAsks asks, CancellationToken ct) =>
            (await asks.AskAsync(id, body?.File, body?.Line, body?.EndLine, body?.Text ?? string.Empty, AdoConnectEndpoints.Login(user), ct).ConfigureAwait(false))
                .ToHttpResult(a => TypedResults.Accepted($"/api/reviews/{id}", new ReviewAskVm(a.Id, a.File, a.Line, a.EndLine, a.Question, a.Answer, a.Author, a.AskedAt))))
            .WithName("AskAboutReview").Produces<ReviewAskVm>(StatusCodes.Status202Accepted).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);

        reviews.MapPost("/{id:long}/send", async (long id, SendRequest? body, [FromServices] ReviewSessionSend send, CancellationToken ct) =>
            (await send.SendAsync(id, body?.Destination ?? string.Empty, ct).ConfigureAwait(false))
                .ToHttpResult(s => TypedResults.Ok(new ReviewSentVm(s.Destination, s.Text, s.Url, s.Posted, s.AsPerson))))
            .WithName("SendReview").Produces<ReviewSentVm>().ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    /// <param name="Destination"><c>pr</c> (a PR's review: post it there) or <c>text</c> (copy it).</param>
    public sealed record SendRequest(string? Destination);

    /// <param name="Repo">A registered repository.</param>
    /// <param name="PullRequestId">Review this PR…</param>
    /// <param name="Branch">…or this branch (against its base, or <paramref name="Base"/>)…</param>
    /// <param name="Base">…or from this commit (with <paramref name="Head"/>); for a branch, what it's compared with.</param>
    /// <param name="Head">…to this commit.</param>
    public sealed record StartReviewRequest(string? Repo, int? PullRequestId, string? Branch, string? Base, string? Head);

    /// <param name="Decision">kept, dropped or edited.</param>
    /// <param name="Text">The new text, for edited.</param>
    public sealed record DecisionRequest(string? Decision, string? Text);

    /// <param name="File">The file (none: the whole change).</param>
    /// <param name="Line">The first line.</param>
    /// <param name="EndLine">The last line.</param>
    /// <param name="Text">The comment, or the question (Ask).</param>
    public sealed record CommentRequest(string? File, int? Line, int? EndLine, string? Text);
}

/// <summary>A finding and the person's decision about it.</summary>
public sealed record ReviewFindingVm(int Number, string Severity, string? File, int? Line, string Title, string? Detail, string? Suggestion, string Decision, string? Edited);

/// <summary>A review session: what it looks at (pinned commits), its state, the reviewer's summary and findings.</summary>
public sealed record ReviewSessionVm(
    long Id, string Repo, string Target, int? PullRequestId, string? HeadRef, string? BaseRef, string? BaseCommit, string? HeadCommit,
    string Status, string? Error, string? Model, string? Effort, string? Summary, IReadOnlyList<ReviewFindingVm> Findings, string CreatedBy,
    string? SentTo, DateTimeOffset CreatedAt)
{
    public static ReviewSessionVm From(ReviewSession s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Id, s.Repository, s.Target, s.PullRequestId, s.HeadRef, s.BaseRef, s.BaseCommit, s.HeadCommit, s.Status, s.Error, s.Model, s.Effort,
            s.Summary, [.. s.Findings.Select((f, i) => new ReviewFindingVm(i + 1, f.Severity, f.File, f.Line, f.Title, f.Detail, f.Suggestion, f.Decision, f.Edited))],
            s.CreatedBy, s.SentTo, s.CreatedAt);
    }
}

public sealed record ReviewCommentVm(long Id, string? File, int? Line, int? EndLine, string Text, string Author, DateTimeOffset CreatedAt)
{
    public static ReviewCommentVm From(ReviewComment c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.Id, c.File, c.Line, c.EndLine, c.Text, c.Author, c.CreatedAt);
    }
}

public sealed record ReviewAskVm(long Id, string? File, int? Line, int? EndLine, string Question, string? Answer, string Author, DateTimeOffset AskedAt);

/// <summary>A session with its comments and questions, as the review page shows it.</summary>
public sealed record ReviewSessionDetailVm(ReviewSessionVm Session, IReadOnlyList<ReviewCommentVm> Comments, IReadOnlyList<ReviewAskVm> Asks)
{
    public static ReviewSessionDetailVm From(ReviewSessionView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return new(ReviewSessionVm.From(v.Session), [.. v.Comments.Select(ReviewCommentVm.From)],
            [.. v.Asks.Select(a => new ReviewAskVm(a.Id, a.File, a.Line, a.EndLine, a.Question, a.Answer, a.Author, a.AskedAt))]);
    }
}

/// <summary>What Send did: the text to copy, or the PR it was posted to (under your name when <c>asPerson</c>).</summary>
public sealed record ReviewSentVm(string Destination, string? Text, Uri? Url, int Posted, bool AsPerson);

/// <summary>The pinned change; <c>unifiedDiff</c> is null when it's too big (only the file list).</summary>
public sealed record ReviewDiffVm(string BaseCommit, string HeadCommit, IReadOnlyList<string> Files, string? UnifiedDiff, bool Truncated);

/// <summary>The registered repositories' active PRs, newest first; <c>failed</c>: repositories that couldn't be read.</summary>
public sealed record OpenPullRequestsVm(IReadOnlyList<OpenPullRequestVm> Items, IReadOnlyList<RepositoryProblemVm> Failed, DateTimeOffset FetchedAt)
{
    internal static OpenPullRequestsVm From(OpenPullRequestList list) => new(
        [.. list.Items.Select(i => new OpenPullRequestVm(i.Repo, i.PullRequest.Id, i.PullRequest.Title, i.PullRequest.Author, i.PullRequest.SourceBranch,
            i.PullRequest.TargetBranch, i.PullRequest.IsDraft, i.PullRequest.CreatedAt, i.PullRequest.Url))],
        [.. list.Failed.Select(f => new RepositoryProblemVm(f.Repo, f.Reason))],
        list.FetchedAt);
}

/// <summary>An active PR.</summary>
public sealed record OpenPullRequestVm(string Repo, int Id, string Title, string Author, string SourceBranch, string TargetBranch, bool IsDraft, DateTimeOffset CreatedAt, Uri Url);

/// <summary>A repository whose PRs couldn't be listed, and why.</summary>
public sealed record RepositoryProblemVm(string Repo, string Reason);
