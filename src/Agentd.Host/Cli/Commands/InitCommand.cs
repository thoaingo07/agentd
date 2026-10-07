using System.CommandLine;
using Agentd.Application.Setup;
using Agentd.Domain.Common;

namespace Agentd.Host.Cli.Commands;

/// <summary>
/// <c>agentd init</c>: the setup wizard in the terminal, for servers without a browser (docs/architect/deployment.md §5a).
/// Every step goes through <see cref="SetupService"/>, like the web wizard. Interactive by default (Enter keeps a
/// value, secrets are typed at a hidden prompt); <c>--non-interactive</c> takes values from options and secrets only
/// from environment variables, never the command line.
/// </summary>
internal static class InitCommand
{
    public const string DatabaseVariable = "AGENTD_INIT_DATABASE";
    public const string PatVariable = "AGENTD_INIT_ADO_PAT";
    public const string ClaudeTokenVariable = "AGENTD_INIT_CLAUDE_TOKEN";
    public const string DiscordTokenVariable = "AGENTD_INIT_DISCORD_TOKEN";

    public static Command Create(CliContext context)
    {
        var nonInteractive = new Option<bool>("--non-interactive") { Description = $"No prompts: values from the options, secrets from {DatabaseVariable}, {PatVariable}, {ClaudeTokenVariable}, {DiscordTokenVariable}." };
        var organization = new Option<string?>("--organization") { Description = "Azure DevOps organization (a name or URL)." };
        var project = new Option<string?>("--project") { Description = "Azure DevOps project." };
        var auth = new Option<string?>("--auth") { Description = "Azure DevOps sign-in: Pat or AzCli." };
        var sshKey = new Option<bool>("--generate-ssh-key") { Description = "Create agentd's SSH key if it has none." };
        var guild = new Option<string?>("--discord-guild") { Description = "Discord server ID (turns chat on)." };
        var channel = new Option<string?>("--discord-channel") { Description = "Discord channel ID." };
        var userName = new Option<string?>("--user-name") { Description = "Your name in agentd (with --discord-user)." };
        var discordUser = new Option<string?>("--discord-user") { Description = "Your Discord user ID." };
        var repos = new Option<string[]>("--repo") { Description = "A repository clone URL to add (repeatable).", AllowMultipleArgumentsPerToken = false };
        var noFinish = new Option<bool>("--no-finish") { Description = "Save and check, but don't mark setup complete." };
        var command = new Command("init", "Set this server up in the terminal: database, Azure DevOps, git key, Claude, chat, repositories, then a review.")
        {
            nonInteractive, organization, project, auth, sshKey, guild, channel, userName, discordUser, repos, noFinish,
        };
        command.SetAction(async (parse, ct) =>
        {
            var run = new InitRun(context, context.Services.GetRequiredService<SetupService>(), Environment.UserName);
            return parse.GetValue(nonInteractive)
                ? await run.NonInteractiveAsync(
                    new InitValues(parse.GetValue(organization), parse.GetValue(project), parse.GetValue(auth), parse.GetValue(sshKey), parse.GetValue(guild),
                        parse.GetValue(channel), parse.GetValue(userName), parse.GetValue(discordUser), parse.GetValue(repos) ?? [], !parse.GetValue(noFinish)),
                    ct).ConfigureAwait(false)
                : await run.InteractiveAsync(ct).ConfigureAwait(false);
        });
        return command;
    }

    private sealed record InitValues(
        string? Organization, string? Project, string? Auth, bool GenerateSshKey, string? Guild, string? Channel,
        string? UserName, string? DiscordUser, string[] Repos, bool Finish);

    private sealed class InitRun(CliContext context, SetupService setup, string by)
    {
        public async Task<int> InteractiveAsync(CancellationToken ct)
        {
            await Say("agentd init: Enter keeps a value. Secrets are typed hidden and stored encrypted.").ConfigureAwait(false);

            await Step("1/7 Database (PostgreSQL)").ConfigureAwait(false);
            var connection = Secret(setup.GetDatabase().ConnectionString.Set ? "Connection string (Enter keeps the saved one): " : "Connection string (Host=…;Port=5432;Username=…;Password=…;Database=…): ");
            if (!string.IsNullOrWhiteSpace(connection))
            {
                await Report(await setup.SaveDatabaseAsync(connection, by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            var database = await setup.TestDatabaseAsync(null, ct).ConfigureAwait(false);
            await Mark(database).ConfigureAwait(false);
            if (database.Ok && database.Fix is not null && Confirm("Create or update the schema now?", yes: true))
            {
                await Mark(await setup.MigrateDatabaseAsync(by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            await Step("2/7 Azure DevOps").ConfigureAwait(false);
            var ado = setup.GetAzureDevOps();
            var organization = Ask("Organization (a name or URL)", ado.Organization);
            var project = Ask("Project", ado.Project);
            var auth = Ask("Sign in with a personal access token (Pat) or az login (AzCli)", ado.Auth);
            var pat = string.Equals(auth, SetupService.PatAuth, StringComparison.OrdinalIgnoreCase)
                ? Secret(ado.Pat.Set ? "Personal access token (Enter keeps the saved one): " : "Personal access token (Work Items + Code read & write, Build read): ")
                : null;
            if (!string.IsNullOrWhiteSpace(organization) && !string.IsNullOrWhiteSpace(project))
            {
                await Report(await setup.SaveAzureDevOpsAsync(new AzureDevOpsInput(organization, project, auth ?? SetupService.AzCliAuth, pat), by, ct).ConfigureAwait(false)).ConfigureAwait(false);
                await Mark(await setup.TestAzureDevOpsAsync(null, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            await Step("3/7 Git access").ConfigureAwait(false);
            if (!setup.GetGitKey().Exists && Confirm("Generate agentd's own SSH key (recommended on a server)?", yes: true))
            {
                await Report(await setup.GenerateGitKeyAsync(by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            await ShowKey(setup.GetGitKey()).ConfigureAwait(false);

            await Step("4/7 Claude").ConfigureAwait(false);
            var claude = await setup.GetClaudeAsync(ct).ConfigureAwait(false);
            await Say(claude.Server.Installed
                ? $"  Claude Code {claude.Server.Version}; on this server: {(claude.Server.LoggedIn ? $"logged in ({claude.Server.Method})" : "not logged in")}."
                : "  Claude Code isn't installed: npm install -g @anthropic-ai/claude-code").ConfigureAwait(false);
            var token = Secret(claude.Token.Set ? "Token from `claude setup-token` (Enter keeps the saved one): " : "Token from `claude setup-token` (Enter to use the server's login): ");
            if (!string.IsNullOrWhiteSpace(token))
            {
                await Report(await setup.SaveClaudeTokenAsync(token, by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            await Mark(await setup.TestClaudeAsync(null, ct).ConfigureAwait(false)).ConfigureAwait(false);

            await Step("5/7 Chat (optional)").ConfigureAwait(false);
            var chat = setup.GetChat();
            if (Confirm("Use Discord?", yes: chat.Enabled))
            {
                var input = new ChatInput(
                    true,
                    Ask("Server ID", chat.GuildId),
                    Ask("Channel ID", chat.ChannelId),
                    Secret(chat.BotToken.Set ? "Bot token (Enter keeps the saved one): " : "Bot token: "),
                    Ask("Your name in agentd", Environment.UserName),
                    Ask("Your Discord user ID (Enter to skip)", null));
                await Report(await setup.SaveChatAsync(input, by, ct).ConfigureAwait(false)).ConfigureAwait(false);
                if (Confirm("Send a test message?", yes: true))
                {
                    await Mark(await setup.TestChatAsync(input, ct).ConfigureAwait(false)).ConfigureAwait(false);
                }
            }
            else if (chat.Enabled)
            {
                await Report(await setup.SaveChatAsync(new ChatInput(false, null, null, null, null, null), by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            await Step("6/7 Repositories").ConfigureAwait(false);
            foreach (var repository in setup.GetRepositories())
            {
                await Say($"  {repository.Name} ({repository.BaseBranch}) {repository.Url}").ConfigureAwait(false);
            }

            while (Ask("Add a repository clone URL (Enter when done)", null) is { Length: > 0 } url)
            {
                await AddRepository(url, ct).ConfigureAwait(false);
            }

            return await Finish(Confirm, ct).ConfigureAwait(false);
        }

        public async Task<int> NonInteractiveAsync(InitValues values, CancellationToken ct)
        {
            var missing = new List<string>();
            var connection = Env(DatabaseVariable);
            if (connection is null && !setup.GetDatabase().ConnectionString.Set)
            {
                missing.Add($"the database: {DatabaseVariable}");
            }

            var ado = setup.GetAzureDevOps();
            var organization = values.Organization ?? ado.Organization;
            var project = values.Project ?? ado.Project;
            var auth = values.Auth ?? ado.Auth;
            missing.AddRange(organization is null ? ["--organization"] : []);
            missing.AddRange(project is null ? ["--project"] : []);
            var pat = Env(PatVariable);
            if (string.Equals(auth, SetupService.PatAuth, StringComparison.OrdinalIgnoreCase) && pat is null && !ado.Pat.Set)
            {
                missing.Add($"the Azure DevOps token: {PatVariable} (or --auth AzCli)");
            }

            var discordToken = Env(DiscordTokenVariable);
            if (values.Guild is not null && (values.Channel is null || (discordToken is null && !setup.GetChat().BotToken.Set)))
            {
                missing.Add($"for Discord: --discord-channel and {DiscordTokenVariable}");
            }

            if (missing.Count > 0)
            {
                await context.Error.WriteLineAsync($"agentd init --non-interactive is missing:{Environment.NewLine}{string.Join(Environment.NewLine, missing.Select(m => $"  - {m}"))}").ConfigureAwait(false);
                return ExitCodes.Usage;
            }

            if (connection is not null)
            {
                await Report(await setup.SaveDatabaseAsync(connection, by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            if (await setup.TestDatabaseAsync(null, ct).ConfigureAwait(false) is { Ok: true, Fix: not null })
            {
                await Mark(await setup.MigrateDatabaseAsync(by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            await Report(await setup.SaveAzureDevOpsAsync(new AzureDevOpsInput(organization!, project!, auth, pat), by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            if (values.GenerateSshKey && !setup.GetGitKey().Exists)
            {
                await Report(await setup.GenerateGitKeyAsync(by, ct).ConfigureAwait(false)).ConfigureAwait(false);
                await ShowKey(setup.GetGitKey()).ConfigureAwait(false);
            }

            if (Env(ClaudeTokenVariable) is { } token)
            {
                await Report(await setup.SaveClaudeTokenAsync(token, by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            if (values.Guild is not null)
            {
                await Report(await setup.SaveChatAsync(new ChatInput(true, values.Guild, values.Channel, discordToken, values.UserName, values.DiscordUser), by, ct).ConfigureAwait(false)).ConfigureAwait(false);
            }

            foreach (var url in values.Repos)
            {
                await AddRepository(url, ct).ConfigureAwait(false);
            }

            return await Finish((_, _) => values.Finish, ct).ConfigureAwait(false);
        }

        /// <summary>The review; then, if every required check passes and the caller agrees, setup is completed.</summary>
        private async Task<int> Finish(Func<string, bool, bool> confirm, CancellationToken ct)
        {
            await Step("7/7 Review").ConfigureAwait(false);
            var review = await setup.ReviewAsync(ct).ConfigureAwait(false);
            foreach (var item in review)
            {
                await Mark(item.Check, $"{item.Title}: ", warnOnly: !item.Required).ConfigureAwait(false);
            }

            if (review.Any(i => i.Required && !i.Check.Ok))
            {
                await context.Error.WriteLineAsync("Not finished: fix the ❌ above, then run `agentd init` again (saved values are kept).").ConfigureAwait(false);
                return ExitCodes.Error;
            }

            if (!confirm("Finish setup?", true))
            {
                await Say("Saved. Run `agentd init` again to finish.").ConfigureAwait(false);
                return ExitCodes.Ok;
            }

            var finished = await setup.FinishAsync(by, ct).ConfigureAwait(false);
            if (!finished.IsSuccess)
            {
                await context.Error.WriteLineAsync(finished.Error.Message).ConfigureAwait(false);
                return ExitCodes.Error;
            }

            await Say("agentd is set up. Restart the daemon to use these settings: agentd daemon restart").ConfigureAwait(false);
            return ExitCodes.Ok;
        }

        private async Task AddRepository(string url, CancellationToken ct)
        {
            var added = await setup.AddRepositoryAsync(new RepositoryInput(url, null, null, null, null), by, ct).ConfigureAwait(false);
            await (added.IsSuccess
                ? Say($"  ✅ added {added.Value!.Name} (base branch {added.Value.BaseBranch}, tag {added.Value.MatchTag ?? "-"}); cloned at the next start")
                : Say($"  ❌ {added.Error!.Message}")).ConfigureAwait(false);
        }

        private async Task ShowKey(GitKeyStep key)
        {
            if (key.Exists)
            {
                await Say($"  Public key ({key.Fingerprint}):{Environment.NewLine}  {key.PublicKey}{Environment.NewLine}  Add it in Azure DevOps → User settings → SSH public keys (or as a GitHub deploy key with write access).").ConfigureAwait(false);
            }
        }

        private string? Env(string name) => context.Environment(name) is { Length: > 0 } v ? v.Trim() : null;

        private string? Secret(string prompt) => context.ReadSecret(prompt)?.Trim();

        private string? Ask(string prompt, string? current)
        {
            var answer = context.ReadLine(current is null ? $"{prompt}: " : $"{prompt} [{current}]: ")?.Trim();
            return string.IsNullOrEmpty(answer) ? current : answer;
        }

        private bool Confirm(string prompt, bool yes)
        {
            var answer = context.ReadLine($"{prompt} [{(yes ? "Y/n" : "y/N")}]: ")?.Trim();
            return string.IsNullOrEmpty(answer) ? yes : answer.StartsWith('y') || answer.StartsWith('Y');
        }

        private Task Say(string text) => context.Out.WriteLineAsync(text);

        private Task Step(string title) => context.Out.WriteLineAsync($"{Environment.NewLine}{title}");

        private Task Mark(StepCheck check, string prefix = "", bool warnOnly = false) =>
            Say(check.Ok
                ? $"  ✅ {prefix}{check.Message}"
                : $"  {(warnOnly ? "⚠️" : "❌")} {prefix}{check.Message}{(check.Fix is null ? string.Empty : $"{Environment.NewLine}     fix: {check.Fix}")}");

        private Task Report<T>(Result<T> result) => result.IsSuccess ? Task.CompletedTask : Say($"  ❌ {result.Error!.Message}");
    }
}
