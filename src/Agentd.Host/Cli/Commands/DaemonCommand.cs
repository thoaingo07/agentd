using System.CommandLine;
using System.Globalization;

namespace Agentd.Host.Cli.Commands;

/// <summary>
/// <c>agentd daemon run</c> (the foreground daemon, what systemd and Docker execute) and the systemd user service around it:
/// <c>install</c>, <c>uninstall</c>, <c>start</c>, <c>stop</c>, <c>restart</c>, <c>status</c>, <c>logs</c> (T1b.5).
/// </summary>
internal static class DaemonCommand
{
    private static readonly string[][] s_enable = [["--user", "daemon-reload"], ["--user", "enable", SystemdUnit.Name]];

    public static Command Create(CliContext context, AgentdCli.Daemon daemon)
    {
        var run = new Command("run", "Run the daemon in the foreground: web UI, MCP endpoint, polling and agents. Extra arguments go to the web host (e.g. --urls).")
        {
            TreatUnmatchedTokensAsErrors = false,
        };
        run.SetAction((parse, ct) => daemon([.. parse.UnmatchedTokens], ct));

        return new Command("daemon", "Run agentd, or manage it as a systemd user service.")
        {
            run,
            Install(context),
            Uninstall(context),
            Systemctl(context, "start", "Start the service."),
            Systemctl(context, "stop", "Stop the service (running agents get a minute to end cleanly)."),
            Systemctl(context, "restart", "Restart the service (e.g. after `agentd secrets set`)."),
            Systemctl(context, "status", "Show whether the service runs.", interactive: true),
            Logs(context),
        };
    }

    private static Command Install(CliContext context)
    {
        var command = new Command("install", "Install agentd as a systemd user service (starts at login, or at boot with lingering) and enable it.");
        command.SetAction(async (_, ct) =>
        {
            if (await RequireSystemdAsync(context, ct).ConfigureAwait(false) is { } missing)
            {
                return missing;
            }

            var dir = SystemdUnit.Directory(context.Environment);
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, SystemdUnit.Name);
            var home = context.Environment(ConfigHome.Variable);
            await File.WriteAllTextAsync(path, SystemdUnit.Render(SystemdUnit.ExecStart(Environment.ProcessPath!, System.Reflection.Assembly.GetEntryAssembly()?.Location), home), ct).ConfigureAwait(false);
            foreach (var args in s_enable)
            {
                var result = await context.Run("systemctl", args, interactive: false, ct).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    await context.Error.WriteLineAsync($"systemctl {string.Join(' ', args)} failed: {result.Output}").ConfigureAwait(false);
                    return ExitCodes.Error;
                }
            }

            await context.Out.WriteLineAsync($"Installed {path} and enabled it. Start it with: agentd daemon start").ConfigureAwait(false);
            await LingerHintAsync(context, ct).ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Uninstall(CliContext context)
    {
        var command = new Command("uninstall", "Stop and disable the service and remove its unit file. agentd's home (~/.agentd) is kept.");
        command.SetAction(async (_, ct) =>
        {
            if (await RequireSystemdAsync(context, ct).ConfigureAwait(false) is { } missing)
            {
                return missing;
            }

            await context.Run("systemctl", ["--user", "disable", "--now", SystemdUnit.Name], interactive: false, ct).ConfigureAwait(false);
            var path = Path.Combine(SystemdUnit.Directory(context.Environment), SystemdUnit.Name);
            var existed = File.Exists(path);
            File.Delete(path);
            await context.Run("systemctl", ["--user", "daemon-reload"], interactive: false, ct).ConfigureAwait(false);
            await context.Out.WriteLineAsync(existed ? $"Removed {path}. agentd's home and data are kept." : "agentd wasn't installed as a service.").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    private static Command Systemctl(CliContext context, string verb, string description, bool interactive = false)
    {
        var command = new Command(verb, description);
        command.SetAction(async (_, ct) =>
        {
            if (await RequireSystemdAsync(context, ct).ConfigureAwait(false) is { } missing)
            {
                return missing;
            }

            var result = await context.Run("systemctl", ["--user", verb, SystemdUnit.Name], interactive, ct).ConfigureAwait(false);
            if (result.Output.Length > 0)
            {
                await (result.ExitCode == 0 ? context.Out : context.Error).WriteLineAsync(result.Output).ConfigureAwait(false);
            }

            // `status` exits 3 for a stopped service: that's an answer, not an error.
            return result.ExitCode == 0 || (verb == "status" && result.ExitCode == 3) ? ExitCodes.Ok : ExitCodes.Error;
        });
        return command;
    }

    private static Command Logs(CliContext context)
    {
        var follow = new Option<bool>("--follow", "-f") { Description = "Keep printing new lines (Ctrl+C to stop)." };
        var lines = new Option<int>("--lines", "-n") { Description = "How many recent lines.", DefaultValueFactory = _ => 200 };
        var command = new Command("logs", "Show the service's log (journald).") { follow, lines };
        command.SetAction(async (parse, ct) =>
        {
            if (await RequireSystemdAsync(context, ct).ConfigureAwait(false) is { } missing)
            {
                return missing;
            }

            List<string> args = ["--user", "-u", SystemdUnit.Name, "-n", parse.GetValue(lines).ToString(CultureInfo.InvariantCulture), "--no-pager"];
            if (parse.GetValue(follow))
            {
                args.Add("-f");
            }

            var result = await context.Run("journalctl", args, interactive: true, ct).ConfigureAwait(false);
            return result.ExitCode == 0 ? ExitCodes.Ok : ExitCodes.Error;
        });
        return command;
    }

    /// <summary>null when systemd's user manager is there; otherwise a message and the exit code.</summary>
    private static async Task<int?> RequireSystemdAsync(CliContext context, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || (await context.Run("systemctl", ["--user", "--version"], interactive: false, ct).ConfigureAwait(false)).ExitCode != 0)
        {
            await context.Error.WriteLineAsync("The service needs Linux with systemd (user services). Elsewhere run `agentd daemon run` (or use the Docker image).").ConfigureAwait(false);
            return ExitCodes.Error;
        }

        return null;
    }

    /// <summary>Without lingering, a user service stops when the user logs out and doesn't start at boot.</summary>
    private static async Task LingerHintAsync(CliContext context, CancellationToken ct)
    {
        var user = context.Environment("USER") ?? Environment.UserName;
        var linger = await context.Run("loginctl", ["show-user", user, "--property=Linger"], interactive: false, ct).ConfigureAwait(false);
        if (!linger.Output.Contains("Linger=yes", StringComparison.Ordinal))
        {
            await context.Out.WriteLineAsync($"To keep it running after you log out and start it at boot, run once: sudo loginctl enable-linger {user}").ConfigureAwait(false);
        }
    }
}
