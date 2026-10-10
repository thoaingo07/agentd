using System.Text.Json.Serialization;

namespace Agentd.Application.Reviews;

/// <summary>What a review session looks at (docs/architect/review-sessions.md §1).</summary>
public static class ReviewTargets
{
    public const string PullRequest = "pr";
    public const string Branch = "branch";
    public const string Range = "range";
    public const string Upload = "upload";
}

/// <summary>Where a review session is: Reviewing → Ready → Sent (or Closed); Failed with a reason.</summary>
public static class ReviewSessionStatus
{
    public const string Reviewing = "Reviewing";
    public const string Ready = "Ready";
    public const string Sent = "Sent";
    public const string Closed = "Closed";
    public const string Failed = "Failed";
}

/// <summary>What the person decided about a finding: kept (default), dropped, or edited (their text replaces it).</summary>
public static class FindingDecisions
{
    public const string Kept = "kept";
    public const string Dropped = "dropped";
    public const string Edited = "edited";

    public static bool IsValid(string? decision) => decision is Kept or Dropped or Edited;
}

/// <summary>A reviewer's finding (today's <see cref="ReviewFinding"/> fields) and the person's decision about it.</summary>
public sealed record SessionFinding(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("suggestion")] string? Suggestion,
    [property: JsonPropertyName("decision")] string Decision = FindingDecisions.Kept,
    [property: JsonPropertyName("edited")] string? Edited = null)
{
    public static SessionFinding From(ReviewFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        return new(finding.Severity, finding.File, finding.Line, finding.Title, finding.Detail, finding.Suggestion);
    }
}

/// <param name="Id">The session.</param>
/// <param name="Repository">The registered repository's name.</param>
/// <param name="Target">One of <see cref="ReviewTargets"/>.</param>
/// <param name="PullRequestId">The PR, for a PR target.</param>
/// <param name="HeadRef">The branch (or commit) asked for.</param>
/// <param name="BaseRef">What it's compared with.</param>
/// <param name="BaseCommit">Pinned when it starts, so the diff, findings and comments agree.</param>
/// <param name="HeadCommit">Pinned head.</param>
/// <param name="Status">One of <see cref="ReviewSessionStatus"/>.</param>
/// <param name="Error">Why it failed.</param>
/// <param name="Model">The reviewer's model.</param>
/// <param name="Effort">The reviewer's effort.</param>
/// <param name="Summary">The reviewer's one-paragraph summary.</param>
/// <param name="Findings">In the reviewer's order (worst first), with decisions.</param>
/// <param name="PullRequestReviewId">The chat <c>!review</c> it belongs to, if any.</param>
/// <param name="Worktree">Its read-only checkout, while it's open.</param>
/// <param name="CreatedBy">Who started it.</param>
/// <param name="SentTo">Where Send delivered it.</param>
/// <param name="CreatedAt">When it started.</param>
/// <param name="UpdatedAt">Last change.</param>
public sealed record ReviewSession(
    long Id, string Repository, string Target, int? PullRequestId, string? HeadRef, string? BaseRef, string? BaseCommit, string? HeadCommit,
    string Status, string? Error, string? Model, string? Effort, string? Summary, IReadOnlyList<SessionFinding> Findings,
    long? PullRequestReviewId, string? Worktree, string CreatedBy, string? SentTo, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>A person's note on a file and lines (no file: the whole change).</summary>
public sealed record ReviewComment(long Id, string? File, int? Line, int? EndLine, string Text, string Author, DateTimeOffset CreatedAt);

/// <summary>
/// A question about the code (on a selection or the whole change) and, once answered, the answer. A thread's first question
/// has no <see cref="ThreadId"/> and holds the agent's session (<see cref="AgentSession"/>); follow-ups name the first question.
/// </summary>
public sealed record ReviewAsk(long Id, string? File, int? Line, int? EndLine, string Question, string? Answer, string Author, DateTimeOffset AskedAt, DateTimeOffset? AnsweredAt,
    long? ThreadId = null, Guid? AgentSession = null);

/// <summary>Review sessions via <c>agentd.review_session_*</c>, <c>review_comment_*</c> and <c>review_ask_*</c> routines.</summary>
public interface IReviewSessionStore
{
    Task<long> InsertAsync(string repository, string target, int? pullRequestId, string? headRef, string? baseRef, string createdBy, string? model, string? effort, CancellationToken cancellationToken);

    Task<ReviewSession?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Someone's recent sessions, newest first.</summary>
    Task<IReadOnlyList<ReviewSession>> ListAsync(string createdBy, int limit, CancellationToken cancellationToken);

    /// <summary>Sessions in <paramref name="status"/>, oldest first (the reviews to resume after a restart).</summary>
    Task<IReadOnlyList<ReviewSession>> ListByStatusAsync(string status, CancellationToken cancellationToken);

    Task PinAsync(long id, string baseCommit, string headCommit, string? worktree, CancellationToken cancellationToken);

    /// <param name="id">The session.</param>
    /// <param name="status">One of <see cref="ReviewSessionStatus"/>.</param>
    /// <param name="reason">Why it failed (Failed), else null.</param>
    /// <param name="sentTo">Where Send delivered it (Sent), else null to keep it.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    Task SetStatusAsync(long id, string status, string? reason, string? sentTo, CancellationToken cancellationToken);

    /// <summary>Appends findings as they arrive (the summary too, when given); returns how many there are now.</summary>
    Task<int> AddFindingsAsync(long id, IReadOnlyList<SessionFinding> findings, string? summary, CancellationToken cancellationToken);

    /// <summary>Records a decision on finding <paramref name="index"/> (0-based); false when there's no such finding.</summary>
    Task<bool> DecideAsync(long id, int index, string decision, string? edited, CancellationToken cancellationToken);

    Task<long> AddCommentAsync(long sessionId, string? file, int? line, int? endLine, string text, string author, CancellationToken cancellationToken);

    /// <summary>Only its author removes a comment; false otherwise.</summary>
    Task<bool> DeleteCommentAsync(long sessionId, long commentId, string author, CancellationToken cancellationToken);

    /// <summary>Only its author rewords a comment; false otherwise.</summary>
    Task<bool> UpdateCommentAsync(long sessionId, long commentId, string author, string text, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewComment>> ListCommentsAsync(long sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// A new thread, or with <paramref name="threadId"/> a follow-up in it: the new id, or <see cref="NoThread"/> /
    /// <see cref="ThreadBusy"/> (its last question waits for the answer).
    /// </summary>
    Task<long> AddAskAsync(long sessionId, string? file, int? line, int? endLine, string question, string author, long? threadId, CancellationToken cancellationToken);

    /// <summary>The thread's agent session started over.</summary>
    Task SetAskSessionAsync(long threadId, Guid session, CancellationToken cancellationToken);

    public const long NoThread = 0;

    public const long ThreadBusy = -1;

    Task AnswerAsync(long askId, string answer, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewAsk>> ListAsksAsync(long sessionId, CancellationToken cancellationToken);
}
