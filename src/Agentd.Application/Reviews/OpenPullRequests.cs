using System.Collections.Concurrent;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Reviews;

/// <summary>An active PR of a registered repository.</summary>
public sealed record OpenPullRequest(string Repo, PullRequestSummary PullRequest);

/// <summary>The active PRs (newest first), the repositories that couldn't be read and why, and when it was read.</summary>
public sealed record OpenPullRequestList(IReadOnlyList<OpenPullRequest> Items, IReadOnlyList<RepositoryProblem> Failed, DateTimeOffset FetchedAt);

/// <summary>A repository whose PRs couldn't be listed.</summary>
public sealed record RepositoryProblem(string Repo, string Reason);

/// <summary>
/// The Reviews page's open PRs (docs/architect/review-sessions.md §3.1): every registered repository's active PRs, read
/// with agentd's identity, in parallel. Each repository's list is kept for <see cref="Fresh"/>, so pages refreshing on
/// their own (and several people) cost Azure DevOps one call per repository per minute.
/// </summary>
public sealed class OpenPullRequests(IRepositoryRegistry repositories, IPullRequestService pullRequests, IClock clock)
{
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(1);
    public const int PerRepository = 50;

    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<OpenPullRequestList> ListAsync(CancellationToken cancellationToken)
    {
        var repos = await repositories.ListAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;
        var reads = repos.Select(r => (Repo: r, List: Cached(r, now))).ToList();
        var items = new List<OpenPullRequest>();
        var failed = new List<RepositoryProblem>();
        foreach (var (repo, list) in reads)
        {
            try
            {
                items.AddRange((await list.WaitAsync(cancellationToken).ConfigureAwait(false)).Select(p => new OpenPullRequest(repo.Name.Value, p)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed.Add(new RepositoryProblem(repo.Name.Value, ex.Message));
            }
        }

        return new OpenPullRequestList([.. items.OrderByDescending(i => i.PullRequest.CreatedAt)], failed, now);
    }

    /// <summary>One read per repository at a time: callers within <see cref="Fresh"/> share it; a failed read is retried next time.</summary>
    private Task<IReadOnlyList<PullRequestSummary>> Cached(Repository repo, DateTimeOffset now)
    {
        var key = repo.Name.Value;
        while (true)
        {
            var hit = _cache.GetValueOrDefault(key);
            if (hit is not null && now - hit.At < Fresh && hit.List.Value is { IsFaulted: false, IsCanceled: false } list)
            {
                return list;
            }

            // Lazy: only the caller whose entry is stored starts the read. Not tied to one caller's cancellation:
            // others may be waiting on the same read.
            var fresh = new Entry(new Lazy<Task<IReadOnlyList<PullRequestSummary>>>(() => pullRequests.ListActiveAsync(repo, PerRepository, CancellationToken.None)), now);
            if (hit is null ? _cache.TryAdd(key, fresh) : _cache.TryUpdate(key, fresh, hit))
            {
                return fresh.List.Value;
            }
        }
    }

    private sealed record Entry(Lazy<Task<IReadOnlyList<PullRequestSummary>>> List, DateTimeOffset At);
}
