using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Reviews;

/// <summary>A line that matches a text search: its file, 1-based line, a snippet and where the match sits in it.</summary>
public sealed record TextHit(string Path, int Line, string Snippet, int MatchStart, int MatchLength);

/// <summary>A file at a commit, for reading: its text (cut at the cap, saying so), or binary.</summary>
public sealed record CommitFile(string Path, string Text, bool Truncated, bool Binary);

/// <summary>Reads a repository at a commit, not just its diff (implemented in Infrastructure.Git with the server's clone).</summary>
public interface IBranchSearch
{
    /// <summary>Every file path at <paramref name="commit"/>.</summary>
    Task<IReadOnlyList<string>> ListFilesAsync(Repository repository, string commit, CancellationToken cancellationToken);

    /// <summary>Lines containing <paramref name="text"/> literally (binary files skipped), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<TextHit>> GrepAsync(Repository repository, string commit, string text, bool ignoreCase, int limit, CancellationToken cancellationToken);

    /// <summary>The file at <paramref name="commit"/>, or null when there's no such file.</summary>
    Task<CommitFile?> ReadFileAsync(Repository repository, string commit, string path, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>
/// Search on the review page (docs/architect/review-sessions.md §2): file names and text across the whole repository at
/// the session's head commit, and a file read there, so a reviewer can look beyond the changed files.
/// </summary>
public sealed class ReviewSearch(IReviewSessionStore store, Ports.IRepositoryRegistry repositories, IBranchSearch search)
{
    public const int MaxResults = 200;
    public const int MaxQuery = 200;
    public const int MaxFileBytes = 512 * 1024;

    /// <summary>File paths containing every word of <paramref name="query"/> (case ignored), file-name matches first.</summary>
    public async Task<Result<IReadOnlyList<string>>> FilesAsync(long id, string query, int limit, CancellationToken ct)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || query.Length > MaxQuery)
        {
            return DomainError.Validation($"Search for 1 to {MaxQuery} characters.");
        }

        var at = await HeadAsync(id, ct).ConfigureAwait(false);
        if (!at.IsSuccess)
        {
            return at.Error;
        }

        var (repo, commit) = at.Value;
        var paths = await search.ListFilesAsync(repo, commit, ct).ConfigureAwait(false);
        return Result.Success<IReadOnlyList<string>>([.. paths
            .Where(p => words.All(w => p.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => Name(p).Contains(words[0], StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(p => p.Length)
            .ThenBy(p => p, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, MaxResults))]);
    }

    /// <summary>Lines containing <paramref name="query"/> literally, across the repository at the head commit.</summary>
    public async Task<Result<IReadOnlyList<TextHit>>> TextAsync(long id, string query, bool ignoreCase, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > MaxQuery || query.Contains('\n', StringComparison.Ordinal))
        {
            return DomainError.Validation($"Search for 1 to {MaxQuery} characters on one line.");
        }

        var at = await HeadAsync(id, ct).ConfigureAwait(false);
        return at.IsSuccess
            ? Result.Success<IReadOnlyList<TextHit>>(await search.GrepAsync(at.Value.Repo, at.Value.Commit, query, ignoreCase, Math.Clamp(limit, 1, MaxResults), ct).ConfigureAwait(false))
            : at.Error;
    }

    /// <summary>One file at the head commit, or the base (<paramref name="atBase"/>: a deleted file), read-only, cut at <see cref="MaxFileBytes"/>.</summary>
    public async Task<Result<CommitFile>> FileAsync(long id, string path, CancellationToken ct, bool atBase = false)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1000 || path.StartsWith('/') || path.StartsWith('-') || path.Contains('\0', StringComparison.Ordinal)
            || path.Split('/').Any(s => s is ".." or "."))
        {
            return DomainError.Validation("That isn't a file path in the repository.");
        }

        var at = await HeadAsync(id, ct, atBase).ConfigureAwait(false);
        if (!at.IsSuccess)
        {
            return at.Error;
        }

        return await search.ReadFileAsync(at.Value.Repo, at.Value.Commit, path, MaxFileBytes, ct).ConfigureAwait(false) is { } file
            ? file
            : DomainError.NotFound($"{path} at {at.Value.Commit[..Math.Min(7, at.Value.Commit.Length)]}");
    }

    /// <summary>The session's repository and head commit (or its base commit, <paramref name="atBase"/>).</summary>
    private async Task<Result<(Repository Repo, string Commit)>> HeadAsync(long id, CancellationToken ct, bool atBase = false)
    {
        if (await store.GetAsync(id, ct).ConfigureAwait(false) is not { } session)
        {
            return DomainError.NotFound($"Review {id}");
        }

        if ((atBase ? session.BaseCommit : session.HeadCommit) is not { Length: > 0 } head)
        {
            return DomainError.Conflict("The review hasn't pinned its commits yet.");
        }

        return RepositoryName.Create(session.Repository) is { IsSuccess: true } name && await repositories.GetAsync(name.Value, ct).ConfigureAwait(false) is { } repo
            ? (repo, head)
            : DomainError.NotFound($"Repository '{session.Repository}'");
    }

    private static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];
}
