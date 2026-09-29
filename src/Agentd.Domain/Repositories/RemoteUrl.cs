using System.Text.RegularExpressions;
using Agentd.Domain.Common;

namespace Agentd.Domain.Repositories;

/// <summary>
/// A repository's git remote URL, parsed into Azure DevOps coordinates. Accepts:
/// <c>git@ssh.dev.azure.com:v3/{org}/{project}/{repo}</c>, <c>{org}@vs-ssh.visualstudio.com:v3/{org}/{project}/{repo}</c>,
/// SSH host aliases such as <c>git@erm-azdo:v3/{org}/{project}/{repo}</c>,
/// <c>https://dev.azure.com/{org}/{project}/_git/{repo}</c> and <c>https://{org}.visualstudio.com/{project}/_git/{repo}</c>.
/// </summary>
public sealed partial record RemoteUrl(string Value, AzureDevOpsRepo AzureDevOps)
{
    public static Result<RemoteUrl> Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return DomainError.Validation("Repository URL is required.");
        }

        var value = url.Trim();
        if (SshV3().Match(value) is { Success: true } ssh)
        {
            return Create(value, ssh.Groups["org"].Value, ssh.Groups["project"].Value, ssh.Groups["repo"].Value);
        }

        if (HttpsDevAzure().Match(value) is { Success: true } https)
        {
            return Create(value, https.Groups["org"].Value, https.Groups["project"].Value, https.Groups["repo"].Value);
        }

        if (HttpsVisualStudio().Match(value) is { Success: true } vs)
        {
            return Create(value, vs.Groups["org"].Value, vs.Groups["project"].Value, vs.Groups["repo"].Value);
        }

        return DomainError.Validation($"'{value}' is not a supported Azure DevOps repository URL.");
    }

    public override string ToString() => Value;

    private static RemoteUrl Create(string value, string org, string project, string repo) =>
        new(value, new AzureDevOpsRepo(Uri.UnescapeDataString(org), Uri.UnescapeDataString(project), Uri.UnescapeDataString(repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repo[..^4] : repo)));

    // [user@]host:v3/org/project/repo   (host may be ssh.dev.azure.com, vs-ssh.visualstudio.com or an ~/.ssh/config alias)
    [GeneratedRegex(@"^(?:ssh://)?(?:[^@/\s]+@)?[^:/\s]+(?::22)?[:/]v3/(?<org>[^/\s]+)/(?<project>[^/\s]+)/(?<repo>[^/\s]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex SshV3();

    [GeneratedRegex(@"^https://(?:[^@/\s]+@)?dev\.azure\.com/(?<org>[^/\s]+)/(?<project>[^/\s]+)/_git/(?<repo>[^/\s?#]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsDevAzure();

    [GeneratedRegex(@"^https://(?:[^@/\s]+@)?(?<org>[^./\s]+)\.visualstudio\.com/(?:DefaultCollection/)?(?<project>[^/\s]+)/_git/(?<repo>[^/\s?#]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsVisualStudio();
}
