using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Ports;

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

/// <summary>Pull requests on the repository's provider (Azure DevOps first).</summary>
public interface IPullRequestService
{
    Task<PullRequestRef?> FindOpenAsync(Repository repository, BranchName source, CancellationToken cancellationToken);

    Task<PullRequestRef> CreateAsync(Repository repository, BranchName source, string target, string title, string description, WorkItemId workItem, CancellationToken cancellationToken);
}
