using System.Diagnostics;
using System.Text;
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
/// Runs <c>git</c> with an argument list (never a shell string), no interactive prompts, a timeout, and
/// agentd's SSH key when present. Arguments are never logged with credentials because none are passed.
/// </summary>
public sealed class GitCli(IOptions<GitOptions> options)
{
    public async Task<GitResult> RunAsync(string? workingDirectory, IReadOnlyList<string> args, CancellationToken cancellationToken, bool throwOnError = true)
    {
        ArgumentNullException.ThrowIfNull(args);
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
}
