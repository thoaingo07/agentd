using System.Text.Json.Serialization;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Monitor;

/// <summary>A prepared fix round waiting for push / discard in the 👀 thread (docs/architect/pr-reviewer-and-monitor.md §2.0).</summary>
/// <param name="Worktree">The checkout with the fix committed.</param>
/// <param name="Commit">The fix's commit.</param>
/// <param name="Summary">What failed and what changed, as asked in the thread.</param>
/// <param name="Threads">PR comment threads the fix addresses (replied to on push).</param>
/// <param name="ExpiresAt">Dropped when nobody answered by then.</param>
public sealed record PendingFix(
    [property: JsonPropertyName("worktree")] string Worktree,
    [property: JsonPropertyName("commit")] string Commit,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("threads")] IReadOnlyList<int> Threads,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt);

/// <summary>A watched PR: who asked, its 👀 thread, and the monitor's bookkeeping.</summary>
public sealed record PrWatch(
    long Id, string Repository, int PullRequestId, string Title, string WatchedBy, ProviderKey Provider, string ThreadId, string? SpaceId,
    bool Active, int FixRounds, IReadOnlyList<int> SeenComments, int? LastBuildId, DateTimeOffset? SignalAt, PendingFix? Pending,
    DateTimeOffset CreatedAt);

/// <summary>The watch list, via <c>agentd.pr_watch_*</c> routines.</summary>
public interface IPrWatchStore
{
    /// <summary>Watches a PR; when it's already watched, the existing watch's id and <c>Created = false</c>.</summary>
    Task<(long Id, bool Created)> InsertAsync(string repository, int pullRequestId, string title, string watchedBy, ProviderKey provider, string threadId, string? spaceId,
        IReadOnlyList<int> seenComments, CancellationToken cancellationToken);

    Task<PrWatch?> GetAsync(long id, CancellationToken cancellationToken);

    Task<PrWatch?> FindActiveAsync(string repository, int pullRequestId, CancellationToken cancellationToken);

    Task<PrWatch?> FindByThreadAsync(ProviderKey provider, string threadId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PrWatch>> ListActiveAsync(CancellationToken cancellationToken);

    /// <summary>Stops watching; false when it wasn't active.</summary>
    Task<bool> StopAsync(long id, CancellationToken cancellationToken);

    Task UpdateAsync(long id, int fixRounds, IReadOnlyList<int> seenComments, int? lastBuildId, DateTimeOffset? signalAt, PendingFix? pending, CancellationToken cancellationToken);
}
