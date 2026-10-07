using System.CommandLine;
using System.Reflection;
using System.Runtime.InteropServices;
using Agentd.Host.Cli.Update;

namespace Agentd.Host.Cli.Commands;

/// <summary><c>agentd update [--channel stable|beta] [--check] [--to x.y.z]</c> and <c>agentd version</c> (T1b.7).</summary>
internal static class UpdateCommand
{
    /// <summary>"0.2.0+3f1c2ab" → the version agentd was built as (<c>0.0.0-dev</c> for a local build).</summary>
    public static string BuiltVersion =>
        typeof(UpdateCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";

    public static Command Version(CliContext context)
    {
        var command = new Command("version", "Show agentd's version, platform and commit.");
        command.SetAction(async (_, _) =>
        {
            var (version, commit) = Split(BuiltVersion);
            await context.Out.WriteLineAsync($"agentd {version} ({RuntimeInformation.RuntimeIdentifier}{(commit is null ? string.Empty : $", commit {commit[..Math.Min(7, commit.Length)]}")})").ConfigureAwait(false);
            return ExitCodes.Ok;
        });
        return command;
    }

    public static Command Update(CliContext context, Func<HttpClient>? http = null, string? executable = null)
    {
        var channel = new Option<string>("--channel") { Description = "stable (default) or beta (includes pre-releases).", DefaultValueFactory = _ => "stable" };
        channel.AcceptOnlyFromAmong("stable", "beta");
        var check = new Option<bool>("--check") { Description = "Only say whether a newer release exists." };
        var to = new Option<string?>("--to") { Description = "Move to this version (e.g. 0.2.0), also an older one." };
        var command = new Command("update", "Update agentd to a newer release from GitHub (checksum verified), migrate the database and restart the service.") { channel, check, to };
        command.SetAction(async (parse, ct) =>
        {
            var exe = executable ?? Environment.ProcessPath!;
            if (executable is null && File.Exists(Path.Combine(AppContext.BaseDirectory, "agentd.dll")))
            {
                await context.Error.WriteLineAsync("This is a development build (agentd.dll next to it): update it from source, or install a release with install.sh.").ConfigureAwait(false);
                return ExitCodes.Error;
            }

            var current = SemVersion.Parse(Split(BuiltVersion).Version) ?? new SemVersion(0, 0, 0, "dev");
            var wanted = parse.GetValue(to) is { Length: > 0 } v ? SemVersion.Parse(v) : null;
            if (parse.GetValue(to) is { Length: > 0 } && wanted is null)
            {
                await context.Error.WriteLineAsync($"`{parse.GetValue(to)}` isn't a version (use e.g. 0.2.0).").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            // Dispose only a client made here, never one handed in.
            using var owned = http is null ? new HttpClient { Timeout = TimeSpan.FromMinutes(10) } : null;
            var client = owned ?? http!();
            var releases = await new GitHubReleases(client, context.Environment("AGENTD_UPDATE_REPOSITORY") ?? GitHubReleases.DefaultRepository, context.Environment("GITHUB_TOKEN"))
                .ListAsync(ct).ConfigureAwait(false);
            if (GitHubReleases.Pick(releases, parse.GetValue(channel)!, current, wanted) is not { } release)
            {
                await context.Out.WriteLineAsync(wanted is null ? $"agentd {current} is up to date ({parse.GetValue(channel)} channel)." : $"There's no release {wanted}.").ConfigureAwait(false);
                return wanted is null ? ExitCodes.Ok : ExitCodes.Error;
            }

            if (parse.GetValue(check))
            {
                await context.Out.WriteLineAsync($"agentd {release.Version} is available (you have {current}). Run: agentd update{(release.Prerelease ? " --channel beta" : string.Empty)}").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            await context.Out.WriteLineAsync($"Updating agentd {current} → {release.Version}…").ConfigureAwait(false);
            if (await new Updater(client, exe, RuntimeInformation.RuntimeIdentifier).ApplyAsync(release, ct).ConfigureAwait(false) is { } problem)
            {
                await context.Error.WriteLineAsync($"Update failed: {problem}.").ConfigureAwait(false);
                return ExitCodes.Error;
            }

            await context.Out.WriteLineAsync($"Installed {release.Version} (the old binary is {Updater.Previous(exe)}). Migrating the database…").ConfigureAwait(false);
            var migrated = await context.Run(exe, ["db", "migrate"], interactive: true, ct).ConfigureAwait(false);
            if (migrated.ExitCode != 0)
            {
                await context.Error.WriteLineAsync("The database migration failed: fix it and run `agentd db migrate` before restarting.").ConfigureAwait(false);
                return ExitCodes.Error;
            }

            if (File.Exists(Path.Combine(SystemdUnit.Directory(context.Environment), SystemdUnit.Name)))
            {
                var restarted = await context.Run("systemctl", ["--user", "restart", SystemdUnit.Name], interactive: false, ct).ConfigureAwait(false);
                await context.Out.WriteLineAsync(restarted.ExitCode == 0
                    ? "Restarted the service: running jobs resume where they were."
                    : $"Restarting the service failed: {restarted.Output}. Run: agentd daemon restart").ConfigureAwait(false);
            }
            else
            {
                await context.Out.WriteLineAsync("Restart the daemon to run the new version.").ConfigureAwait(false);
            }

            return ExitCodes.Ok;
        });
        return command;
    }

    private static (string Version, string? Commit) Split(string informational)
    {
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? (informational, null) : (informational[..plus], informational[(plus + 1)..]);
    }
}
