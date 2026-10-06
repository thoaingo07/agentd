using System.Diagnostics;
using System.Text;

namespace Agentd.Host.Cli;

/// <summary>A finished process: its exit code and (unless it ran interactively) its output.</summary>
internal sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Runs a program; <paramref name="interactive"/> lets it write straight to the terminal (status, logs -f).</summary>
internal delegate Task<ProcessResult> RunProcess(string file, IReadOnlyList<string> args, bool interactive, CancellationToken ct);

/// <summary>The systemd <b>user</b> service that runs <c>agentd daemon run</c> (docs/architect/deployment.md §1, T1b.5).</summary>
internal static class SystemdUnit
{
    public const string Name = "agentd.service";

    /// <summary><c>$XDG_CONFIG_HOME/systemd/user</c>, or <c>~/.config/systemd/user</c>.</summary>
    public static string Directory(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var config = environment("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(environment("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(config, "systemd", "user");
    }

    /// <summary>The command the service runs: this executable (or <c>dotnet agentd.dll</c> in a development build).</summary>
    public static IReadOnlyList<string> ExecStart(string processPath, string? entryAssembly) =>
        Path.GetFileNameWithoutExtension(processPath) == "dotnet" && !string.IsNullOrEmpty(entryAssembly)
            ? [processPath, entryAssembly, "daemon", "run"]
            : [processPath, "daemon", "run"];

    /// <summary>
    /// The unit. It holds no secrets (they live in <c>~/.agentd/config/secrets.json</c>); only <c>AGENTD_HOME</c> when it isn't
    /// the default. Restarts on failure, and stops gracefully (SIGTERM, up to a minute) so running agents end cleanly.
    /// </summary>
    public static string Render(IReadOnlyList<string> execStart, string? agentdHome)
    {
        ArgumentNullException.ThrowIfNull(execStart);
        var unit = new StringBuilder()
            .AppendLine("# Written by `agentd daemon install`. No secrets here: they're in ~/.agentd/config/secrets.json (agentd secrets set).")
            .AppendLine("[Unit]")
            .AppendLine("Description=agentd: coding agents for Azure DevOps work items")
            .AppendLine("After=network-online.target")
            .AppendLine("Wants=network-online.target")
            .AppendLine()
            .AppendLine("[Service]")
            .AppendLine("Type=simple")
            .Append("ExecStart=").AppendLine(string.Join(' ', execStart.Select(Quote)))
            .AppendLine("WorkingDirectory=%h")
            .AppendLine("Restart=on-failure")
            .AppendLine("RestartSec=5")
            .AppendLine("KillSignal=SIGTERM")
            .AppendLine("TimeoutStopSec=60");
        if (!string.IsNullOrWhiteSpace(agentdHome))
        {
            unit.Append("Environment=").AppendLine(Quote($"{ConfigHome.Variable}={agentdHome}"));
        }

        return unit
            .AppendLine()
            .AppendLine("[Install]")
            .AppendLine("WantedBy=default.target")
            .ToString();
    }

    /// <summary>systemd quoting: double quotes, backslash escapes, and <c>%</c> doubled (it starts a specifier).</summary>
    internal static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("%", "%%", StringComparison.Ordinal) + "\"";

    /// <summary>The real process runner.</summary>
    public static async Task<ProcessResult> Run(string file, IReadOnlyList<string> args, bool interactive, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = !interactive, RedirectStandardError = !interactive };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi)!;
            var output = interactive ? Task.FromResult(string.Empty) : process.StandardOutput.ReadToEndAsync(ct);
            var error = interactive ? Task.FromResult(string.Empty) : process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, (await output.ConfigureAwait(false) + await error.ConfigureAwait(false)).Trim());
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ProcessResult(127, ex.Message);   // not installed
        }
    }
}
