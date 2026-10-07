using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Infrastructure.AzureDevOps;
using Agentd.Infrastructure.Claude;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agentd.Host.Cli.Commands;

/// <summary>One doctor check: what was checked, whether it passed, and how to fix it. A warning passes (exit 0) but is shown.</summary>
internal sealed record DoctorResult(string Check, bool Ok, string Detail, string? Fix = null, bool Warning = false);

internal static class DoctorCommand
{
    public static Command Create(CliContext context)
    {
        var repo = new Option<string?>("--repo") { Description = "Also check the toolchains this repository needs (dotnet, npm, helm, …)." };
        var skipLive = new Option<bool>("--skip-live") { Description = "Don't send the tiny test prompt to Claude." };
        var command = new Command("doctor", "Check everything agentd needs and say how to fix what's missing (deployment.md §6).") { repo, skipLive };
        command.SetAction(async (parse, ct) =>
        {
            var results = new List<DoctorResult>();
            foreach (var check in Checks(context, parse.GetValue(repo), live: !parse.GetValue(skipLive)))
            {
                IReadOnlyList<DoctorResult> checkResults;
                try
                {
                    checkResults = await check(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    checkResults = [new("doctor", false, $"a check crashed: {ex.Message}")];
                }

                results.AddRange(checkResults);
                foreach (var result in checkResults)
                {
                    await context.Out.WriteAsync(Format(result)).ConfigureAwait(false);
                }
            }

            var failed = results.Count(r => !r.Ok);
            var warned = results.Count(r => r.Ok && r.Warning);
            await context.Out.WriteLineAsync(failed == 0
                ? warned == 0 ? "All checks passed." : $"All checks passed, with {warned} warning(s)."
                : $"{failed} check(s) failed{(warned == 0 ? "" : $", {warned} warning(s)")}.").ConfigureAwait(false);
            return failed == 0 ? ExitCodes.Ok : ExitCodes.DoctorFailed;
        });
        return command;
    }

    internal static string Format(DoctorResult r) =>
        $"{(!r.Ok ? "❌" : r.Warning ? "⚠️" : "✅")} {r.Check}: {r.Detail}\n" + ((r.Ok && !r.Warning) || r.Fix is null ? "" : $"   fix: {r.Fix}\n");

    private static IEnumerable<Func<CancellationToken, Task<IReadOnlyList<DoctorResult>>>> Checks(CliContext context, string? repo, bool live)
    {
        var services = context.Services;
        return
        [
            ct => Task.FromResult<IReadOnlyList<DoctorResult>>([Configuration(services)]),
            ct => Task.FromResult<IReadOnlyList<DoctorResult>>([Secrets(context)]),
            async ct => [await PostgresAsync(services, ct).ConfigureAwait(false)],
            async ct => [await MinimumAsync("git", "git", ["--version"], DoctorChecks.MinimumGit, "install git 2.40 or later (e.g. sudo apt install git)", warnOnly: true, ct).ConfigureAwait(false)],
            ct => Task.FromResult<IReadOnlyList<DoctorResult>>([SshKey(services)]),
            async ct => [await AzureDevOpsAsync(services, ct).ConfigureAwait(false)],
            ct => RepositoriesAsync(services, ct),
            async ct => [await MinimumAsync("Node.js", "node", ["--version"], new Version(DoctorChecks.MinimumNode, 0), "install Node.js 24 (https://nodejs.org)", warnOnly: true, ct).ConfigureAwait(false)],
            ct => ClaudeAsync(services, live, ct),
            ct => ChatAsync(services, ct),
            ct => Task.FromResult<IReadOnlyList<DoctorResult>>([Worktrees(services)]),
            async ct => [await NewerReleaseAsync(context, ct).ConfigureAwait(false)],
            ct => repo is null ? Task.FromResult<IReadOnlyList<DoctorResult>>([]) : ToolchainsAsync(services, repo, ct),
        ];
    }

    /// <summary>The options validators the daemon runs at startup (e.g. messaging, users).</summary>
    private static DoctorResult Configuration(IServiceProvider services)
    {
        try
        {
            services.GetService<IStartupValidator>()?.Validate();
            services.GetRequiredService<IOptions<Options.AgentdOptions>>().Value.ToString();
            return new("Configuration", true, "valid");
        }
        catch (OptionsValidationException ex)
        {
            return new("Configuration", false, string.Join("; ", ex.Failures), "fix ~/.agentd/config/agentd.json (or the AGENTD_* variables)");
        }
    }

    private static DoctorResult Secrets(CliContext context)
    {
        var store = new Configuration.SecretStore(context.Home);
        var (values, unreadable) = store.Load();
        return unreadable.Count == 0
            ? new("Secrets", true, values.Count == 0 ? "none stored" : $"{values.Count} stored, all decrypt")
            : new("Secrets", false, $"can't decrypt: {string.Join(", ", unreadable)} (was ~/.agentd/keys replaced?)", $"set them again: agentd secrets set {unreadable[0]}");
    }

    /// <summary>agentd's own SSH key (used for git when present).</summary>
    private static DoctorResult SshKey(IServiceProvider services)
    {
        var key = Infrastructure.Git.GitOptions.Expand(services.GetRequiredService<IOptions<Infrastructure.Git.GitOptions>>().Value.SshKeyPath);
        return File.Exists(key)
            ? new("SSH key", true, $"{key} ({(File.Exists(key + ".pub") ? "public key next to it" : "no .pub next to it")})")
            : new("SSH key", true, $"none at {key}: git uses your ~/.ssh setup", $"for a server: ssh-keygen -t ed25519 -N '' -f {key}, then add {key}.pub to Azure DevOps (User settings → SSH public keys)", Warning: true);
    }

    private static async Task<IReadOnlyList<DoctorResult>> ChatAsync(IServiceProvider services, CancellationToken ct)
    {
        var enabled = services.GetRequiredService<Application.Messaging.IMessagingProviderRegistry>().Enabled;
        if (enabled.Count == 0)
        {
            return [new("Chat", true, "no provider enabled (chat is optional)", Warning: false)];
        }

        var results = new List<DoctorResult>();
        foreach (var provider in enabled)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var health = await provider.CheckHealthAsync(timeout.Token).ConfigureAwait(false);
                results.Add(new($"Chat {provider.Key}", health.Healthy, health.Detail, "check the bot token (agentd secrets set …) and the channel id"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || timeout.IsCancellationRequested)
            {
                results.Add(new($"Chat {provider.Key}", false, ex.Message, "check the network and the bot token"));
            }
        }

        return results;
    }

    /// <summary>A warning when a newer release exists (only for installed releases, not local builds).</summary>
    private static async Task<DoctorResult> NewerReleaseAsync(CliContext context, CancellationToken ct)
    {
        var current = Update.SemVersion.Parse(UpdateCommand.BuiltVersion.Split('+')[0]);
        if (current is null || current.Pre == "dev")
        {
            return new("Version", true, $"{UpdateCommand.BuiltVersion.Split('+')[0]} (a local build)");
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var releases = await new Update.GitHubReleases(http, context.Environment("AGENTD_UPDATE_REPOSITORY") ?? Update.GitHubReleases.DefaultRepository, context.Environment("GITHUB_TOKEN"))
                .ListAsync(ct).ConfigureAwait(false);
            return Update.GitHubReleases.Pick(releases, "stable", current, null) is { } newer
                ? new("Version", true, $"{current}; {newer.Version} is available", "run: agentd update", Warning: true)
                : new("Version", true, $"{current} (up to date)");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new("Version", true, $"{current} (couldn't check for updates: {ex.Message})", Warning: true);
        }
    }

    /// <summary><c>--repo</c>: the tools the repository's files call for are on PATH.</summary>
    private static async Task<IReadOnlyList<DoctorResult>> ToolchainsAsync(IServiceProvider services, string name, CancellationToken ct)
    {
        if (Domain.Jobs.ValueObjects.RepositoryName.Create(name) is not { IsSuccess: true } valid
            || await services.GetRequiredService<IRepositoryRegistry>().GetAsync(valid.Value, ct).ConfigureAwait(false) is not { } repository)
        {
            return [new($"Toolchains {name}", false, "no such repository", "see: agentd repo list")];
        }

        var files = await services.GetRequiredService<IWorktreeManager>().ListFilesAsync(repository, ct).ConfigureAwait(false);
        var needs = DoctorChecks.Toolchains(files);
        if (needs.Count == 0)
        {
            return [new($"Toolchains {name}", true, "no build files recognised (nothing to check)", Warning: true)];
        }

        var results = new List<DoctorResult>();
        foreach (var need in needs)
        {
            var found = await ToolAsync($"{name}: {need.Tool}", need.Tool, need.VersionArgs, need.Install, ct).ConfigureAwait(false);
            results.Add(found with { Detail = found.Ok ? $"{FirstLine(found.Detail)} (for {need.Because})" : $"{found.Detail}; {need.Because} needs it" });
        }

        return results;
    }

    private static async Task<DoctorResult> MinimumAsync(string check, string binary, string[] args, Version minimum, string fix, bool warnOnly, CancellationToken ct)
    {
        var tool = await ToolAsync(check, binary, args, fix, ct).ConfigureAwait(false);
        if (!tool.Ok)
        {
            return tool;
        }

        var version = DoctorChecks.ParseVersion(tool.Detail);
        return version is null || version >= minimum
            ? tool
            : tool with { Ok = warnOnly, Warning = warnOnly, Detail = $"{tool.Detail} (agentd wants {DoctorChecks.Minimum(minimum)} or later)", Fix = fix };
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    /// <summary>Checkouts and their disk use, and the free space where they live (low disk stops builds and clones).</summary>
    internal static DoctorResult Worktrees(IServiceProvider services)
    {
        const string Check = "Worktrees";
        var root = Infrastructure.Git.GitOptions.Expand(services.GetRequiredService<IOptions<Infrastructure.Git.GitOptions>>().Value.WorktreeRoot);
        var folders = Directory.Exists(root) ? Directory.GetDirectories(root).SelectMany(Directory.GetDirectories).ToList() : [];
        long bytes = 0;
        foreach (var file in folders.SelectMany(f => SafeFiles(f)))
        {
            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch (IOException)
            {
            }
        }

        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(Directory.Exists(root) ? root : AppContext.BaseDirectory))!);
        var free = drive.AvailableFreeSpace;
        var low = free < 5L * 1024 * 1024 * 1024 || free < drive.TotalSize / 20;
        var detail = $"{folders.Count} checkout(s), {Size(bytes)}; {Size(free)} free of {Size(drive.TotalSize)} on {drive.Name}";
        return low
            ? new(Check, false, detail, "free disk space: unused checkouts are removed hourly; old failed jobs after Jobs:RetainFailedWorktrees")
            : new(Check, true, detail);
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.#} GB"),
        >= 1L << 20 => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0} MB"),
        _ => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / 1024} KB"),
    };

    private static async Task<DoctorResult> PostgresAsync(IServiceProvider services, CancellationToken ct)
    {
        const string Check = "PostgreSQL";
        try
        {
            var db = services.GetRequiredService<NpgsqlDataSource>();
            await using var cmd = db.CreateCommand("SELECT to_regclass('agentd.jobs') IS NOT NULL");
            var schema = (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            if (!schema)
            {
                return new(Check, false, "connected, but the agentd schema is missing", "run: agentd db migrate");
            }

            var connection = services.GetRequiredService<IConfiguration>().GetConnectionString("agentd")!;
            var pending = await new Migrator.SchemaMigrator(connection, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance).PendingAsync(ct).ConfigureAwait(false);
            return pending.Count == 0
                ? new(Check, true, $"connected; schema up to date ({Migrator.SchemaMigrator.KnownVersions().Count} migrations)")
                : new(Check, false, $"connected; {pending.Count} migration(s) not applied yet ({string.Join(", ", pending)})", "run: agentd db migrate");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(Check, false, ex.Message, "set ConnectionStrings:agentd (or Agentd:Database:ConnectionString) and make sure PostgreSQL is running");
        }
    }

    private static async Task<DoctorResult> AzureDevOpsAsync(IServiceProvider services, CancellationToken ct)
    {
        var ado = services.GetRequiredService<IOptions<AzureDevOpsOptions>>().Value;
        var check = $"Azure DevOps {ado.Organization}/{ado.Project} ({ado.Auth})";
        if (string.IsNullOrWhiteSpace(ado.Organization) || string.IsNullOrWhiteSpace(ado.Project))
        {
            return new("Azure DevOps", false, "no organization/project configured", "set Agentd:AzureDevOps:Organization and Agentd:AzureDevOps:Project");
        }

        try
        {
            var jobs = services.GetRequiredService<IOptions<JobOptions>>().Value;
            var waiting = await services.GetRequiredService<IWorkItemSource>()
                .QueryTaggedAsync(jobs.Tag, jobs.ClaimTag, jobs.States.ToList(), ct).ConfigureAwait(false);
            return new(check, true, $"work items readable; {waiting.Count} tagged '{jobs.Tag}' and waiting");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(check, false, ex.Message, ado.Auth == AzureDevOpsAuth.Pat
                ? "set a PAT with Work Items (read & write) and Code (read & write): AGENTD_AzureDevOps__Pat, never in a committed file"
                : "run: az login   (and check the organization and project names)");
        }
    }

    private static async Task<IReadOnlyList<DoctorResult>> RepositoriesAsync(IServiceProvider services, CancellationToken ct)
    {
        IReadOnlyList<Domain.Repositories.Repository> repos;
        try
        {
            repos = await services.GetRequiredService<IRepositoryRegistry>().ListAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [new("Repositories", false, $"cannot list repositories: {ex.Message}", "fix the PostgreSQL check first")];
        }

        if (repos.Count == 0)
        {
            return [new("Repositories", false, "none registered", "run: agentd repo add <clone url>")];
        }

        var remote = services.GetRequiredService<IGitRemote>();
        var results = new List<DoctorResult>();
        foreach (var repo in repos)
        {
            var check = $"Repository {repo.Name}";
            try
            {
                var head = await remote.GetDefaultBranchAsync(repo.RemoteUrl, ct).ConfigureAwait(false);
                results.Add(new(check, true, $"reachable ({repo.RemoteUrl}, default branch {head}, base {repo.BaseBranch})"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new(check, false, $"{repo.RemoteUrl}: {ex.Message.ReplaceLineEndings(" ")}", "check SSH access (ssh-add, ~/.ssh/config host alias, key registered in Azure DevOps) or the HTTPS credential"));
            }
        }

        return results;
    }

    private static async Task<IReadOnlyList<DoctorResult>> ClaudeAsync(IServiceProvider services, bool live, CancellationToken ct)
    {
        var claude = services.GetRequiredService<IOptions<ClaudeOptions>>().Value;
        var version = await ToolAsync("Claude Code CLI", claude.Binary, ["--version"], "install Claude Code: npm install -g @anthropic-ai/claude-code", ct).ConfigureAwait(false);
        if (!version.Ok)
        {
            return [version];
        }

        if (!string.IsNullOrWhiteSpace(claude.OAuthToken))
        {
            return [version, live ? await LivePromptAsync(claude, "long-lived token (claude setup-token)", ct).ConfigureAwait(false)
                : new("Claude subscription", true, "long-lived token configured (claude setup-token); not tested (--skip-live)", Warning: true)];
        }

        var env = string.IsNullOrWhiteSpace(claude.ConfigDir) ? null : new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = ExpandHome(claude.ConfigDir) };
        var (exit, output) = await RunAsync(claude.Binary, ["auth", "status", "--json"], env, ct).ConfigureAwait(false);
        var status = exit == 0 ? TryParse(output) : null;
        var loggedIn = status?["loggedIn"]?.GetValue<bool>() == true;
        var method = status?["authMethod"]?.GetValue<string>();
        var plan = status?["subscriptionType"]?.GetValue<string>();
        var subscription = loggedIn
            ? new DoctorResult("Claude subscription", true, $"logged in ({method}{(plan is null ? "" : $", {plan}")})")
            : new DoctorResult("Claude subscription", false, "not logged in", "run: claude auth login   (or claude setup-token, then: agentd secrets set Claude:OAuthToken)");
        return !subscription.Ok || !live ? [version, subscription] : [version, await LivePromptAsync(claude, subscription.Detail, ct).ConfigureAwait(false)];
    }

    /// <summary>
    /// A tiny real prompt in exactly the environment agents get (SafeEnvironment: the profile's token or login, no daemon
    /// secrets), so a headless login (a <c>claude setup-token</c> token) is proven to work, not just configured.
    /// </summary>
    private static async Task<DoctorResult> LivePromptAsync(ClaudeOptions claude, string how, CancellationToken ct)
    {
        var env = SafeEnvironment.Build(Environment.GetEnvironmentVariables(), claude).ToDictionary(kv => kv.Key, kv => kv.Value);
        var watch = Stopwatch.StartNew();
        var (exit, output) = await RunAsync(claude.Binary,
            ["-p", "Reply with exactly: agentd-ok", "--max-turns", "1", "--model", "haiku", "--output-format", "json", "--strict-mcp-config", "--tools", ""],
            env, ct, replaceEnvironment: true).ConfigureAwait(false);
        var reply = exit == 0 ? TryParse(output)?["result"]?.GetValue<string>() : null;
        return reply?.Contains("agentd-ok", StringComparison.Ordinal) == true
            ? new("Claude subscription", true, $"{how}: a test prompt answered in {watch.Elapsed.TotalSeconds:0.0} s")
            : new("Claude subscription", false, $"{how}, but a test prompt failed: {(reply ?? output).Trim().ReplaceLineEndings(" ")[..Math.Min(200, (reply ?? output).Trim().Length)]}",
                "check the login (claude auth status) or the token; a usage limit also shows up here");
    }

    private static async Task<DoctorResult> ToolAsync(string check, string binary, string[] args, string fix, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(binary, args, null, ct).ConfigureAwait(false);
        return exit == 0
            ? new(check, true, output.Trim().ReplaceLineEndings(" "))
            : new(check, false, exit == -1 ? $"'{binary}' not found" : output.Trim(), fix);
    }

    /// <summary>Runs a short command; exit code -1 when it cannot be started.</summary>
    private static async Task<(int Exit, string Output)> RunAsync(string binary, string[] args, IReadOnlyDictionary<string, string>? env, CancellationToken ct, bool replaceEnvironment = false)
    {
        var psi = new ProcessStartInfo(binary) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (replaceEnvironment)
        {
            psi.Environment.Clear();
        }

        foreach (var (key, value) in env ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }

        try
        {
            using var process = Process.Start(psi)!;
            process.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, process.ExitCode == 0 ? await stdout.ConfigureAwait(false) : await stderr.ConfigureAwait(false));
        }
        catch (Win32Exception)
        {
            return (-1, "");
        }
    }

    private static string ExpandHome(string path) =>
        path.StartsWith('~') ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..] : path;

    private static JsonNode? TryParse(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
