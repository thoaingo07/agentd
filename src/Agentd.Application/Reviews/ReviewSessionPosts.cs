namespace Agentd.Application.Reviews;

/// <summary>One re-check of a Send: when, at which PR head, and how the posted findings stood after it (or what went wrong).</summary>
public sealed record SessionRecheck(DateTimeOffset At, string? Head, int Fixed, int Open, int Closed, int Added, string? Problem = null);

/// <summary>
/// A review page's Send to a PR (review-sessions.md §4): the findings as posted, each with its thread and status, the main
/// message's thread, the PR head they're about, and the re-checks so far. <see cref="Done"/> once the PR closed.
/// </summary>
public sealed record ReviewSessionPost(long SessionId, int PullRequestId, ReviewResult Posted, string? Head, IReadOnlyList<SessionRecheck> Rechecks, bool Done);

/// <summary>Sends kept for re-checks, via <c>agentd.review_session_post_*</c> routines.</summary>
public interface IReviewSessionPosts
{
    /// <summary>Keeps a Send (sending again replaces it, its history too).</summary>
    Task SetAsync(long sessionId, int pullRequestId, ReviewResult posted, string? head, CancellationToken cancellationToken);

    Task<ReviewSessionPost?> GetAsync(long sessionId, CancellationToken cancellationToken);

    /// <summary>The Sends whose PR is still open, oldest first.</summary>
    Task<IReadOnlyList<ReviewSessionPost>> ListOpenAsync(CancellationToken cancellationToken);

    /// <summary>The findings' statuses at <paramref name="head"/>; <paramref name="recheck"/>, when given, joins the history.</summary>
    Task UpdateAsync(long sessionId, ReviewResult posted, string? head, SessionRecheck? recheck, CancellationToken cancellationToken);

    /// <summary>The PR completed or was abandoned: no more re-checks.</summary>
    Task DoneAsync(long sessionId, CancellationToken cancellationToken);
}
