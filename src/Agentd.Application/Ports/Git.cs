using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Ports;

/// <summary>Queries a git remote without cloning (implemented in Infrastructure.Git).</summary>
public interface IGitRemote
{
    /// <summary>The remote's default branch (from <c>git ls-remote --symref &lt;url&gt; HEAD</c>), e.g. "develop".</summary>
    Task<string> GetDefaultBranchAsync(string remoteUrl, CancellationToken cancellationToken);
}

/// <summary>Managed clones and per-job worktrees (implemented in Infrastructure.Git).</summary>
public interface IWorktreeManager
{
    Task EnsureCloneAsync(Repository repository, CancellationToken cancellationToken);

    Task<WorktreePath> CreateAsync(Repository repository, WorkItemId workItem, BranchName branch, CancellationToken cancellationToken);

    /// <summary>
    /// The work item's worktree, at its usual path, on <paramref name="branch"/> from the latest base branch
    /// (an existing worktree there is replaced). The path stays the same, so the Claude session can resume.
    /// </summary>
    Task<WorktreePath> RecreateAsync(Repository repository, WorkItemId workItem, BranchName branch, CancellationToken cancellationToken);

    Task<bool> HasCommitsAheadAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken);

    /// <summary>Pushes the branch. Never force-pushes.</summary>
    Task PushAsync(WorktreePath worktree, BranchName branch, CancellationToken cancellationToken);

    Task RemoveAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken);

    Task PruneAsync(Repository repository, CancellationToken cancellationToken);

    /// <summary>
    /// A detached, read-only-by-convention checkout of the latest base branch at <c>worktrees/&lt;repo&gt;/&lt;name&gt;</c>
    /// (e.g. an idea's). An existing checkout at that path is reused.
    /// </summary>
    Task<string> CheckoutDetachedAsync(Repository repository, string name, CancellationToken cancellationToken);

    /// <summary>
    /// A detached checkout of <paramref name="commit"/> (after a fetch) at <c>worktrees/&lt;repo&gt;/&lt;name&gt;</c>, e.g. a PR's
    /// head for a review. An existing checkout at that path is moved to the commit (a re-review after new pushes).
    /// </summary>
    Task<string> CheckoutCommitAsync(Repository repository, string name, string commit, CancellationToken cancellationToken);

    /// <summary>
    /// What <paramref name="branch"/> changes against the base branch: the live worktree (including uncommitted
    /// edits) while it exists, otherwise the branch in the managed clone. Null when the branch doesn't exist.
    /// Over <paramref name="maxBytes"/> the diff text is left out and only the file list is returned.
    /// </summary>
    Task<BranchDiff?> DiffAsync(Repository repository, BranchName branch, WorktreePath? worktree, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>A branch's changes against its base, as a unified diff (null when truncated).</summary>
public sealed record BranchDiff(string BaseRef, string HeadRef, IReadOnlyList<string> Files, string? UnifiedDiff, bool Truncated);

public sealed record PullRequestRef(int Id, Uri Url);

/// <summary>What a review needs to know about a pull request.</summary>
/// <param name="Id">The PR number.</param>
/// <param name="Title">The PR title.</param>
/// <param name="Description">The PR description (Markdown), if any.</param>
/// <param name="Author">The creator's display name.</param>
/// <param name="SourceBranch">e.g. <c>feature/x</c> (without <c>refs/heads/</c>).</param>
/// <param name="TargetBranch">e.g. <c>develop</c>.</param>
/// <param name="SourceCommit">The PR head that's reviewed.</param>
/// <param name="Status">Active, completed or abandoned.</param>
/// <param name="IsDraft">A draft PR.</param>
/// <param name="Url">The PR in the browser.</param>
/// <param name="WorkItems">The work items linked to the PR (none when unknown).</param>
public sealed record PullRequestDetails(
    int Id, string Title, string? Description, string Author, string SourceBranch, string TargetBranch, string SourceCommit,
    PullRequestStatus Status, bool IsDraft, Uri Url, IReadOnlyList<int>? WorkItems = null);

public enum PullRequestStatus
{
    Active,
    Completed,
    Abandoned,
}

/// <summary>A human comment in a PR thread (system comments and agentd's own replies are left out).</summary>
/// <param name="ThreadId">The thread; replies go there.</param>
/// <param name="CommentId">Unique within the PR; used to remember what was already handled.</param>
/// <param name="Author">The commenter's display name.</param>
/// <param name="Content">The comment text (Markdown).</param>
/// <param name="FilePath">The file the thread is anchored to, if any.</param>
/// <param name="Line">The line in that file, if any.</param>
/// <param name="ThreadStatus">ADO thread status: active, pending, fixed, wontFix, closed, byDesign.</param>
/// <param name="PublishedAt">When the comment was posted.</param>
public sealed record PullRequestComment(
    int ThreadId,
    int CommentId,
    string Author,
    string Content,
    string? FilePath,
    int? Line,
    string ThreadStatus,
    DateTimeOffset PublishedAt)
{
    /// <summary>Prefix of every comment agentd writes on a PR (it posts with the operator's identity, so the author can't tell).</summary>
    public const string AgentdMarker = "🤖 agentd:";

    /// <summary>A thread still waiting for an answer or a fix.</summary>
    public bool IsOpen => ThreadStatus is "active" or "pending";
}

/// <summary>Pull requests on the repository's provider (Azure DevOps first).</summary>
public interface IPullRequestService
{
    Task<PullRequestRef?> FindOpenAsync(Repository repository, BranchName source, CancellationToken cancellationToken);

    Task<PullRequestRef> CreateAsync(Repository repository, BranchName source, string target, string title, string description, WorkItemId workItem, CancellationToken cancellationToken);

    Task<PullRequestStatus> GetStatusAsync(Repository repository, int pullRequestId, CancellationToken cancellationToken);

    /// <summary>Human comments in the PR's threads, oldest first.</summary>
    Task<IReadOnlyList<PullRequestComment>> ListCommentsAsync(Repository repository, int pullRequestId, CancellationToken cancellationToken);

    /// <summary>Replies in a thread; the text is prefixed with <see cref="PullRequestComment.AgentdMarker"/>.</summary>
    Task ReplyAsync(Repository repository, int pullRequestId, int threadId, string text, CancellationToken cancellationToken);

    /// <summary>The PR, or null when it doesn't exist.</summary>
    Task<PullRequestDetails?> GetAsync(Repository repository, int pullRequestId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a new comment thread, anchored to <paramref name="filePath"/> at <paramref name="line"/> when given (on the PR's
    /// new side), otherwise on the PR itself; the text is prefixed with <see cref="PullRequestComment.AgentdMarker"/>.
    /// Returns the thread id.
    /// </summary>
    Task<int> CreateThreadAsync(Repository repository, int pullRequestId, string text, string? filePath, int? line, CancellationToken cancellationToken);
}
