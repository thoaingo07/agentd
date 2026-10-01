using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Domain.Repositories;

/// <summary>Azure DevOps coordinates of a repository.</summary>
public sealed record AzureDevOpsRepo(string Organization, string Project, string Name);

/// <summary>
/// A repository agentd works on, registered by URL (agentd manages its own clone).
/// Work items match it by an exact <see cref="MatchTag"/> (wins) or the longest <see cref="MatchAreaPaths"/> prefix.
/// </summary>
public sealed record Repository(
    RepositoryName Name,
    string RemoteUrl,
    AzureDevOpsRepo AzureDevOps,
    string BaseBranch,
    string? MatchTag,
    IReadOnlyList<string> MatchAreaPaths)
{
    /// <summary>A registration from a URL; the name defaults to the repository name in the URL.</summary>
    public static Repository From(RemoteUrl url, string? name, string baseBranch, string? matchTag, IReadOnlyList<string>? areaPaths) =>
        new(
            RepositoryName.From(string.IsNullOrWhiteSpace(name) ? url.AzureDevOps.Name : name),
            url.Value,
            url.AzureDevOps,
            baseBranch,
            string.IsNullOrWhiteSpace(matchTag) ? null : matchTag.Trim(),
            areaPaths ?? []);
}
