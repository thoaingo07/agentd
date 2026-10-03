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

    Task<bool> HasCommitsAheadAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken);

    /// <summary>Pushes the branch. Never force-pushes.</summary>
    Task PushAsync(WorktreePath worktree, BranchName branch, CancellationToken cancellationToken);

    Task RemoveAsync(Repository repository, WorktreePath worktree, CancellationToken cancellationToken);

    Task PruneAsync(Repository repository, CancellationToken cancellationToken);
}

public sealed record PullRequestRef(int Id, Uri Url);

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
}
