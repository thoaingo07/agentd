using Agentd.Application.Ports;

namespace Agentd.Infrastructure.Git;

/// <summary>Reads a remote's default branch without cloning (<c>git ls-remote --symref &lt;url&gt; HEAD</c>).</summary>
public sealed class GitRemote(GitCli git) : IGitRemote
{
    public async Task<string> GetDefaultBranchAsync(string remoteUrl, CancellationToken cancellationToken)
    {
        var result = await git.RunAsync(null, ["ls-remote", "--symref", "--", remoteUrl, "HEAD"], cancellationToken).ConfigureAwait(false);
        return ParseDefaultBranch(result.StandardOutput)
            ?? throw new GitException($"Could not determine the default branch of {remoteUrl}.");
    }

    /// <summary>Parses <c>ref: refs/heads/develop\tHEAD</c>.</summary>
    internal static string? ParseDefaultBranch(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("ref: refs/heads/", StringComparison.Ordinal) && l.EndsWith("HEAD", StringComparison.Ordinal))
            .Select(l => l["ref: refs/heads/".Length..].Split('\t', ' ')[0])
            .FirstOrDefault();
}
