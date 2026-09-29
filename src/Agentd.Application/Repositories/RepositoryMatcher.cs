using Agentd.Application.Ports;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Repositories;

/// <summary>Outcome of matching a work item to a registered repository.</summary>
public abstract record RepositoryMatch
{
    public sealed record Matched(Repository Repository) : RepositoryMatch;

    public sealed record NoMatch : RepositoryMatch;

    public sealed record Ambiguous(IReadOnlyList<string> Candidates) : RepositoryMatch;
}

/// <summary>An exact <c>repo:&lt;name&gt;</c>-style tag wins; otherwise the longest area-path prefix.</summary>
public static class RepositoryMatcher
{
    public static RepositoryMatch Match(IReadOnlyList<Repository> repositories, WorkItemDetails item)
    {
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(item);

        var byTag = repositories
            .Where(r => r.MatchTag is { Length: > 0 } tag && item.Tags.Any(t => string.Equals(t.Trim(), tag, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (byTag.Count > 0)
        {
            return Pick(byTag);
        }

        var itemPath = Normalize(item.AreaPath);
        var byArea = repositories
            .Select(r => (Repository: r, Length: r.MatchAreaPaths.Select(Normalize).Where(p => IsPrefix(p, itemPath)).Select(p => p.Length).DefaultIfEmpty(-1).Max()))
            .Where(x => x.Length >= 0)
            .ToList();
        if (byArea.Count == 0)
        {
            return new RepositoryMatch.NoMatch();
        }

        var longest = byArea.Max(x => x.Length);
        return Pick(byArea.Where(x => x.Length == longest).Select(x => x.Repository).ToList());
    }

    private static RepositoryMatch Pick(List<Repository> candidates) =>
        candidates.Count == 1
            ? new RepositoryMatch.Matched(candidates[0])
            : new RepositoryMatch.Ambiguous(candidates.Select(r => r.Name.Value).Order(StringComparer.Ordinal).ToList());

    private static string Normalize(string path) => path.Replace('/', '\\').Trim().TrimEnd('\\');

    private static bool IsPrefix(string prefix, string path) =>
        prefix.Length > 0
        && path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
        && (path.Length == prefix.Length || path[prefix.Length] == '\\');
}
