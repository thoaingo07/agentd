using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Agentd.Application.Ports;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Git;

/// <summary>A failed git command. <see cref="StandardError"/> carries git's message.</summary>
public sealed class GitException : Exception
{
    public GitException()
    {
    }

    public GitException(string message)
        : base(message)
    {
    }

    public GitException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public GitException(string message, int exitCode, string standardError)
        : base(message)
    {
        ExitCode = exitCode;
        StandardError = standardError;
    }

    public int ExitCode { get; }

    public string StandardError { get; } = string.Empty;
}

public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// Runs <c>git</c> with an argument list (never a shell string), no interactive prompts, a timeout, and agentd's SSH key
/// when present. Commands that reach a remote also get the HTTPS credentials (<see cref="IGitHttpCredentials"/>) as
/// environment config, so they're never in the arguments, a log or a clone's config.
/// </summary>
public sealed partial class GitCli(IOptions<GitOptions> options, IGitHttpCredentials? http = null)
{
    /// <summary>Where Azure DevOps is served over HTTPS (the HTTPS credentials go only there).</summary>
    public const string AzureDevOpsPrefix = "https://dev.azure.com/";

    private static readonly HashSet<string> s_network = new(StringComparer.Ordinal) { "clone", "fetch", "pull", "push", "ls-remote" };

    /// <summary>
    /// The address agentd uses for <paramref name="url"/>: an Azure DevOps HTTPS remote in its one canonical form, an
    /// Azure DevOps SSH remote's HTTPS address (<see cref="GitOptions.AzureDevOpsOverHttps"/>), anything else as given.
    /// </summary>
    public string Remote(string url) =>
        CanonicalHttps(url) ?? (options.Value.AzureDevOpsOverHttps && AzureDevOpsHttps(url) is { } https ? https : url);

    /// <summary>
    /// An Azure DevOps HTTPS remote as <c>https://dev.azure.com/org/project/_git/repo</c>: without a user name
    /// (<c>https://org@dev.azure.com/…</c>, which makes git ask for a password) and with the older
    /// <c>https://org.visualstudio.com/[DefaultCollection/]project/_git/repo</c> moved to dev.azure.com, where agentd's
    /// sign-in is sent. Null for anything else.
    /// </summary>
    public static string? CanonicalHttps(string url)
    {
        var value = url?.Trim() ?? string.Empty;
        var m = AdoHttps().Match(value);
        if (!m.Success)
        {
            m = VsHttps().Match(value);
        }

        return m.Success ? $"{AzureDevOpsPrefix}{m.Groups["org"].Value}/{m.Groups["project"].Value}/_git/{m.Groups["repo"].Value}" : null;
    }

    /// <summary>
    /// <c>git@ssh.dev.azure.com:v3/org/project/repo</c>, <c>ssh://git@ssh.dev.azure.com/v3/…</c> or an SSH host alias with a
    /// <c>v3/</c> path → <c>https://dev.azure.com/org/project/_git/repo</c>; null for anything else.
    /// </summary>
    public static string? AzureDevOpsHttps(string url) =>
        AdoSsh().Match(url?.Trim() ?? string.Empty) is { Success: true } m
            ? $"{AzureDevOpsPrefix}{m.Groups["org"].Value}/{m.Groups["project"].Value}/_git/{m.Groups["repo"].Value}"
            : null;

    public async Task<GitResult> RunAsync(string? workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken, bool throwOnError = true)
    {
        ArgumentNullException.ThrowIfNull(args);
        var headers = await HeadersAsync(workingDirectory, args, cancellationToken).ConfigureAwait(false);
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.Environment["GIT_CONFIG_COUNT"] = headers.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < headers.Count; i++)
        {
            psi.Environment[$"GIT_CONFIG_KEY_{i}"] = $"http.{headers[i].UrlPrefix}.extraHeader";
            psi.Environment[$"GIT_CONFIG_VALUE_{i}"] = headers[i].Header;
        }

        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C";
        var key = GitOptions.Expand(options.Value.SshKeyPath);
        if (File.Exists(key))
        {
            var knownHosts = GitOptions.Expand(options.Value.KnownHostsPath);
            psi.Environment["GIT_SSH_COMMAND"] =
                $"ssh -i \"{key}\" -o IdentitiesOnly=yes -o UserKnownHostsFile=\"{knownHosts}\" -o StrictHostKeyChecking=accept-new -o BatchMode=yes";
        }
        else
        {
            psi.Environment.TryAdd("GIT_SSH_COMMAND", "ssh -o BatchMode=yes");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.CommandTimeout);

        using var process = Process.Start(psi) ?? throw new GitException("Failed to start git.");
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new GitException($"git {Describe(args)} timed out after {options.Value.CommandTimeout}.");
        }

        var result = new GitResult(process.ExitCode, (await stdout.ConfigureAwait(false)).TrimEnd(), (await stderr.ConfigureAwait(false)).TrimEnd());
        if (throwOnError && result.ExitCode != 0)
        {
            throw new GitException($"git {Describe(args)} failed ({result.ExitCode}): {result.StandardError}", result.ExitCode, result.StandardError);
        }

        return result;
    }

    private static string Describe(IReadOnlyList<string> args) => args.Count > 0 ? args[0] : string.Empty;

    /// <summary>
    /// The HTTPS credentials for a command that reaches Azure DevOps over HTTPS (its URL in the arguments, or the clone's
    /// origin); none for local commands or other remotes (an SSH remote uses agentd's SSH key instead).
    /// </summary>
    private async ValueTask<IReadOnlyList<GitHttpHeader>> HeadersAsync(string? workingDirectory, IReadOnlyList<string> args, CancellationToken ct)
    {
        // Any Azure DevOps HTTPS remote gets the sign-in, whether it was cloned over HTTPS or moved there from SSH.
        if (http is null || !s_network.Contains(Describe(args)))
        {
            return [];
        }

        var remote = args.FirstOrDefault(a => a.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ?? (workingDirectory is null ? null : (await RunAsync(workingDirectory, ["config", "--get", "remote.origin.url"], ct, throwOnError: false).ConfigureAwait(false)).StandardOutput);
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.Equals("dev.azure.com", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        try
        {
            return await http.GetAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new GitException($"git {Describe(args)}: signing in to Azure DevOps failed: {ex.Message}", ex);
        }
    }

    [GeneratedRegex(@"^(?:ssh://)?(?:[^@/\s]+@)?[^:/\s]+(?::\d+)?[:/]v3/(?<org>[^/\s]+)/(?<project>[^/\s]+)/(?<repo>[^/\s]+?)/?$")]
    private static partial Regex AdoSsh();

    [GeneratedRegex(@"^https://(?:[^@/\s]+@)?dev\.azure\.com/(?<org>[^/\s]+)/(?<project>[^/\s]+)/_git/(?<repo>[^/\s?#]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex AdoHttps();

    [GeneratedRegex(@"^https://(?:[^@/\s]+@)?(?<org>[^./\s]+)\.visualstudio\.com/(?:DefaultCollection/)?(?<project>[^/\s]+)/_git/(?<repo>[^/\s?#]+?)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex VsHttps();
}
