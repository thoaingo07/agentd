using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Agentd.Application.Setup;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Git;

/// <summary>
/// agentd's SSH key at <c>Agentd:Git:SshKeyPath</c> (default <c>~/.agentd/ssh/id_ed25519</c>). <see cref="GitCli"/> uses it for
/// every git command as soon as it exists, so a new key needs no restart.
/// </summary>
public sealed class SshGitKey(IOptions<GitOptions> options, GitCli git) : IGitKey
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private string KeyPath => GitOptions.Expand(options.Value.SshKeyPath);

    public GitKeyInfo? Read()
    {
        var key = KeyPath;
        if (!File.Exists(key) || !File.Exists(key + ".pub"))
        {
            return null;
        }

        var publicKey = File.ReadAllText(key + ".pub").Trim();
        return new GitKeyInfo(key, publicKey, Fingerprint(publicKey));
    }

    public async Task<GitKeyInfo> GenerateAsync(string comment, CancellationToken cancellationToken)
    {
        var key = KeyPath;
        if (File.Exists(key) || File.Exists(key + ".pub"))
        {
            throw new InvalidOperationException($"{key} already exists.");
        }

        var dir = Path.GetDirectoryName(key)!;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(dir);
        }
        else
        {
            Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var psi = new ProcessStartInfo("ssh-keygen") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-q", "-t", "ed25519", "-N", string.Empty, "-C", comment, "-f", key })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ssh-keygen.");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ssh-keygen failed ({process.ExitCode}): {(await error.ConfigureAwait(false)).Trim()}");
        }

        return Read() ?? throw new InvalidOperationException($"ssh-keygen didn't write {key}.pub.");
    }

    public async Task<StepCheck> TestAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TestTimeout);
        try
        {
            var result = await git.RunAsync(null, ["ls-remote", "--heads", "--", url], timeout.Token, throwOnError: false).ConfigureAwait(false);
            if (result.ExitCode == 0)
            {
                var branches = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                return new StepCheck(true, string.Create(CultureInfo.InvariantCulture, $"Reached the repository: {branches} branch(es)."));
            }

            var reason = result.StandardError.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? $"git exited with {result.ExitCode}";
            return new StepCheck(false, reason, Read() is null
                ? "generate agentd's SSH key first (or set up ~/.ssh for the user agentd runs as)"
                : "add the public key to Azure DevOps (User settings → SSH public keys) or as a GitHub deploy key, then test again");
        }
        catch (Exception ex) when (ex is GitException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new StepCheck(false, ex is OperationCanceledException ? "The repository didn't answer in time." : ex.Message, "check the URL and that this server can reach it");
        }
    }

    /// <summary>"SHA256:…" as <c>ssh-keygen -l</c> prints it: the SHA-256 of the key blob, base64 without padding.</summary>
    public static string Fingerprint(string publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        var parts = publicKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var blob = parts.Length >= 2 ? Convert.FromBase64String(parts[1]) : throw new FormatException("Not an OpenSSH public key.");
        return "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
    }
}
