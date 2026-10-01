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

/// <summary>One doctor check: what was checked, whether it passed, and how to fix it.</summary>
internal sealed record DoctorResult(string Check, bool Ok, string Detail, string? Fix = null);

internal static class DoctorCommand
{
    public static Command Create(CliContext context)
    {
        var command = new Command("doctor", "Check PostgreSQL, git, Azure DevOps access, repository access and the Claude CLI.");
        command.SetAction(async (_, ct) =>
        {
            var results = new List<DoctorResult>();
            foreach (var check in Checks(context.Services))
            {
                var checkResults = await check(ct).ConfigureAwait(false);
                results.AddRange(checkResults);
                foreach (var result in checkResults)
                {
                    await context.Out.WriteAsync(Format(result)).ConfigureAwait(false);
                }
            }

            var failed = results.Count(r => !r.Ok);
            await context.Out.WriteLineAsync(failed == 0 ? "All checks passed." : $"{failed} check(s) failed.").ConfigureAwait(false);
            return failed == 0 ? ExitCodes.Ok : ExitCodes.DoctorFailed;
        });
        return command;
    }

    internal static string Format(DoctorResult r) =>
        $"{(r.Ok ? "[ok]  " : "[FAIL]")} {r.Check}: {r.Detail}\n" + (r.Ok || r.Fix is null ? "" : $"       fix: {r.Fix}\n");

    private static IEnumerable<Func<CancellationToken, Task<IReadOnlyList<DoctorResult>>>> Checks(IServiceProvider services) =>
    [
        async ct => [await PostgresAsync(services, ct).ConfigureAwait(false)],
        async ct => [await ToolAsync("git", "git", ["--version"], "install git (e.g. sudo apt install git)", ct).ConfigureAwait(false)],
        async ct => [await AzureDevOpsAsync(services, ct).ConfigureAwait(false)],
        ct => RepositoriesAsync(services, ct),
        ct => ClaudeAsync(services, ct),
    ];

    private static async Task<DoctorResult> PostgresAsync(IServiceProvider services, CancellationToken ct)
    {
        const string Check = "PostgreSQL";
        try
        {
            var db = services.GetRequiredService<NpgsqlDataSource>();
            await using var cmd = db.CreateCommand("SELECT to_regclass('agentd.jobs') IS NOT NULL");
            var schema = (bool)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            return schema
                ? new(Check, true, "connected; schema present")
                : new(Check, false, "connected, but the agentd schema is missing", "run: agentd db migrate");
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

    private static async Task<IReadOnlyList<DoctorResult>> ClaudeAsync(IServiceProvider services, CancellationToken ct)
    {
        var claude = services.GetRequiredService<IOptions<ClaudeOptions>>().Value;
        var version = await ToolAsync("Claude Code CLI", claude.Binary, ["--version"], "install Claude Code: npm install -g @anthropic-ai/claude-code", ct).ConfigureAwait(false);
        if (!version.Ok)
        {
            return [version];
        }

        if (!string.IsNullOrWhiteSpace(claude.OAuthToken))
        {
            return [version, new("Claude subscription", true, "long-lived token configured (claude setup-token)")];
        }

        var env = string.IsNullOrWhiteSpace(claude.ConfigDir) ? null : new Dictionary<string, string> { ["CLAUDE_CONFIG_DIR"] = ExpandHome(claude.ConfigDir) };
        var (exit, output) = await RunAsync(claude.Binary, ["auth", "status", "--json"], env, ct).ConfigureAwait(false);
        var status = exit == 0 ? TryParse(output) : null;
        var loggedIn = status?["loggedIn"]?.GetValue<bool>() == true;
        var method = status?["authMethod"]?.GetValue<string>();
        var plan = status?["subscriptionType"]?.GetValue<string>();
        var subscription = loggedIn
            ? new DoctorResult("Claude subscription", true, $"logged in ({method}{(plan is null ? "" : $", {plan}")})")
            : new DoctorResult("Claude subscription", false, "not logged in", "run: claude auth login   (or claude setup-token and set Agentd:Claude:OAuthToken)");
        return [version, subscription];
    }

    private static async Task<DoctorResult> ToolAsync(string check, string binary, string[] args, string fix, CancellationToken ct)
    {
        var (exit, output) = await RunAsync(binary, args, null, ct).ConfigureAwait(false);
        return exit == 0
            ? new(check, true, output.Trim().ReplaceLineEndings(" "))
            : new(check, false, exit == -1 ? $"'{binary}' not found" : output.Trim(), fix);
    }

    /// <summary>Runs a short command; exit code -1 when it cannot be started.</summary>
    private static async Task<(int Exit, string Output)> RunAsync(string binary, string[] args, IReadOnlyDictionary<string, string>? env, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(binary) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
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
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
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
