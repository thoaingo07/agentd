using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Setup;

/// <summary>The database step: the connection string is a secret, so only its status is shown.</summary>
public sealed record DatabaseStep(SecretStatus ConnectionString);

/// <summary>The Azure DevOps step. <see cref="Auth"/> is <c>Pat</c> or <c>AzCli</c>.</summary>
public sealed record AzureDevOpsStep(string? Organization, string? Project, string Auth, SecretStatus Pat);

/// <param name="Organization">A name or URL (<c>https://dev.azure.com/myorg</c>, <c>https://myorg.visualstudio.com</c>).</param>
/// <param name="Project">The project name.</param>
/// <param name="Auth"><c>Pat</c> or <c>AzCli</c>.</param>
/// <param name="Pat">A new token; null keeps the stored one.</param>
public sealed record AzureDevOpsInput(string Organization, string Project, string Auth, string? Pat);

/// <summary>The git step: agentd's public key, when it has one.</summary>
public sealed record GitKeyStep(bool Exists, string? PublicKey, string? Fingerprint, string? Path)
{
    public static GitKeyStep From(GitKeyInfo? key) => key is null ? new(false, null, null, null) : new(true, key.PublicKey, key.Fingerprint, key.Path);
}

/// <summary>The Claude step: a stored <c>claude setup-token</c> token, and the server's own Claude Code login.</summary>
public sealed record ClaudeStep(SecretStatus Token, ClaudeLogin Server);

/// <summary>A user in <c>Agentd:Users</c> with a Discord identity (agentd only answers people it knows).</summary>
public sealed record ChatUser(string Name, string DiscordId);

/// <summary>The chat step (Discord): the channel agentd works in, the bot token's status, and who it answers.</summary>
public sealed record ChatStep(bool Enabled, string? GuildId, string? ChannelId, SecretStatus BotToken, IReadOnlyList<ChatUser> Users);

/// <param name="Enabled">Off: agentd works without chat (the web UI only).</param>
/// <param name="GuildId">The Discord server's ID.</param>
/// <param name="ChannelId">The channel's ID.</param>
/// <param name="BotToken">A new token; null keeps the saved one.</param>
/// <param name="UserName">Your name in agentd (with <paramref name="UserDiscordId"/>: added as an Admin, or the ID added to that user).</param>
/// <param name="UserDiscordId">Your Discord user ID.</param>
public sealed record ChatInput(bool Enabled, string? GuildId, string? ChannelId, string? BotToken, string? UserName, string? UserDiscordId);

/// <summary>A repository in <c>Agentd:Repositories:Items</c> (the daemon registers and clones these at start).</summary>
public sealed record RepositoryEntry(string Url, string? Name, string? BaseBranch, string? MatchTag, IReadOnlyList<string> MatchAreaPaths);

/// <param name="Url">The clone URL (Azure DevOps, SSH or HTTPS).</param>
/// <param name="Name">Optional; default: the repository's name.</param>
/// <param name="BaseBranch">Optional; default: the remote's default branch.</param>
/// <param name="MatchTag">Optional; default <c>repo:&lt;name&gt;</c> unless area paths are given.</param>
/// <param name="MatchAreaPaths">Work items under these area paths go to this repository.</param>
public sealed record RepositoryInput(string Url, string? Name, string? BaseBranch, string? MatchTag, IReadOnlyList<string>? MatchAreaPaths);

/// <summary>One line of the review: a step, whether it's required to finish, and its check.</summary>
public sealed record ReviewItem(string Step, string Title, bool Required, StepCheck Check);

/// <summary>Setup is finished: the daemon must restart to use the new settings.</summary>
public sealed record FinishResult(DateTimeOffset CompletedAt);

/// <summary>A saved step. The daemon reads these settings at start, so they apply after a restart.</summary>
public sealed record SaveResult(bool RestartRequired);

/// <summary>
/// The setup and settings use cases, shared by the web wizard, Settings and <c>agentd init</c>
/// (docs/architect/deployment.md §5a). Each step has a Save and a Test; secrets are write-only and
/// every change is audited without its value.
/// </summary>
public sealed class SetupService(
    IConfigWriter config,
    ISecrets secrets,
    ISettingsAudit audit,
    IDatabaseProbe database,
    IAzureDevOpsProbe azureDevOps,
    IGitKey gitKey,
    IClaudeProbe claude,
    IChatProbe chat,
    IGitRemote remote,
    ISetupLink link,
    IOptions<JobOptions> jobs,
    TimeProvider time)
{
    public const string ConnectionStringSecret = "ConnectionStrings:agentd";
    public const string PatSecret = "AzureDevOps:Pat";
    public const string ClaudeTokenSecret = "Claude:OAuthToken";
    public const string DiscordTokenSecret = "Messaging:Providers:Discord:BotToken";
    private const string Discord = "Messaging:Providers:Discord";
    public const string PatAuth = "Pat";
    public const string AzCliAuth = "AzCli";

    public DatabaseStep GetDatabase() => new(secrets.Status(ConnectionStringSecret));

    public async Task<Result<SaveResult>> SaveDatabaseAsync(string connectionString, string by, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return DomainError.Validation("Enter the PostgreSQL connection string.");
        }

        if (database.Problem(connectionString) is { } problem)
        {
            return DomainError.Validation(problem);
        }

        secrets.Store(ConnectionStringSecret, connectionString.Trim(), by);
        await audit.RecordAsync([Change(ConnectionStringSecret, "secret", "set", by)], cancellationToken).ConfigureAwait(false);
        return new SaveResult(RestartRequired: true);
    }

    /// <summary>Tests <paramref name="connectionString"/>, or the stored one when it's null.</summary>
    public Task<StepCheck> TestDatabaseAsync(string? connectionString, CancellationToken cancellationToken)
    {
        var value = string.IsNullOrWhiteSpace(connectionString) ? secrets.TryGet(ConnectionStringSecret) : connectionString.Trim();
        if (value is null)
        {
            return Task.FromResult(new StepCheck(false, "No connection string saved yet.", "enter one and save it"));
        }

        return database.Problem(value) is { } problem
            ? Task.FromResult(new StepCheck(false, problem, "check the connection string"))
            : database.TestAsync(value, cancellationToken);
    }

    /// <summary>Creates or updates the schema with the stored connection string.</summary>
    public async Task<StepCheck> MigrateDatabaseAsync(string by, CancellationToken cancellationToken)
    {
        if (secrets.TryGet(ConnectionStringSecret) is not { } value)
        {
            return new StepCheck(false, "No connection string saved yet.", "save the connection string first");
        }

        var result = await database.MigrateAsync(value, cancellationToken).ConfigureAwait(false);
        if (result.Ok)
        {
            await audit.RecordAsync([Change("Database:Schema", "action", "migrated", by)], cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public AzureDevOpsStep GetAzureDevOps() => new(
        config.Read("AzureDevOps:Organization"),
        config.Read("AzureDevOps:Project"),
        string.Equals(config.Read("AzureDevOps:Auth"), PatAuth, StringComparison.OrdinalIgnoreCase) ? PatAuth : AzCliAuth,
        secrets.Status(PatSecret));

    public async Task<Result<SaveResult>> SaveAzureDevOpsAsync(AzureDevOpsInput input, string by, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var connection = Validate(input);
        if (!connection.IsSuccess)
        {
            return connection.Error;
        }

        var c = connection.Value!;
        if (c.UsePat && string.IsNullOrWhiteSpace(input.Pat) && !secrets.Status(PatSecret).Set)
        {
            return DomainError.Validation("Enter a personal access token, or choose az login.");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["AzureDevOps:Organization"] = c.Organization,
            ["AzureDevOps:Project"] = c.Project,
            ["AzureDevOps:Auth"] = c.UsePat ? PatAuth : AzCliAuth,
        };
        await config.SetAsync(values, cancellationToken).ConfigureAwait(false);
        var changes = values.Keys.Select(k => Change(k, "config", "set", by)).ToList();
        if (!string.IsNullOrWhiteSpace(input.Pat))
        {
            secrets.Store(PatSecret, input.Pat.Trim(), by);
            changes.Add(Change(PatSecret, "secret", "set", by));
        }

        await audit.RecordAsync(changes, cancellationToken).ConfigureAwait(false);
        return new SaveResult(RestartRequired: true);
    }

    /// <summary>Tests <paramref name="input"/> (a PAT left empty: the stored one), or the saved settings when it's null.</summary>
    public async Task<StepCheck> TestAzureDevOpsAsync(AzureDevOpsInput? input, CancellationToken cancellationToken)
    {
        var saved = GetAzureDevOps();
        input ??= new AzureDevOpsInput(saved.Organization ?? string.Empty, saved.Project ?? string.Empty, saved.Auth, null);
        var connection = Validate(input);
        if (!connection.IsSuccess)
        {
            return new StepCheck(false, connection.Error!.Message, "fill in the organization and project");
        }

        var c = connection.Value!;
        if (c.UsePat)
        {
            c = c with { Pat = string.IsNullOrWhiteSpace(input.Pat) ? secrets.TryGet(PatSecret) : input.Pat.Trim() };
            if (string.IsNullOrWhiteSpace(c.Pat))
            {
                return new StepCheck(false, "No personal access token given or saved.", "create one with Work Items (read & write), Code (read & write) and Build (read)");
            }
        }

        return await azureDevOps.TestAsync(c, jobs.Value, cancellationToken).ConfigureAwait(false);
    }

    public GitKeyStep GetGitKey() => GitKeyStep.From(gitKey.Read());

    /// <summary>Generates agentd's SSH key; an existing key is never replaced (repositories may already trust it).</summary>
    public async Task<Result<GitKeyStep>> GenerateGitKeyAsync(string by, CancellationToken cancellationToken)
    {
        if (gitKey.Read() is { } existing)
        {
            return DomainError.Conflict($"agentd already has an SSH key ({existing.Path}). To replace it, delete it and its .pub on the server first.");
        }

        var key = await gitKey.GenerateAsync($"agentd@{Environment.MachineName}", cancellationToken).ConfigureAwait(false);
        await audit.RecordAsync([Change("Git:SshKey", "key", "generated", by)], cancellationToken).ConfigureAwait(false);
        return GitKeyStep.From(key);
    }

    /// <summary>Tests access to a repository (its SSH clone URL) with agentd's key, or the user's own when there's none.</summary>
    public Task<StepCheck> TestGitAccessAsync(string? url, CancellationToken cancellationToken)
    {
        var value = url?.Trim();
        if (string.IsNullOrEmpty(value) || value.StartsWith('-') || value.Any(char.IsWhiteSpace))
        {
            return Task.FromResult(new StepCheck(false, "Enter a repository's clone URL.", "e.g. git@ssh.dev.azure.com:v3/<org>/<project>/<repo>"));
        }

        return gitKey.TestAsync(value, cancellationToken);
    }

    public async Task<ClaudeStep> GetClaudeAsync(CancellationToken cancellationToken) =>
        new(secrets.Status(ClaudeTokenSecret), await claude.StatusAsync(cancellationToken).ConfigureAwait(false));

    /// <summary>Stores a <c>claude setup-token</c> token (agents get it as <c>CLAUDE_CODE_OAUTH_TOKEN</c>, nothing else).</summary>
    public async Task<Result<SaveResult>> SaveClaudeTokenAsync(string token, string by, CancellationToken cancellationToken)
    {
        var value = token?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length < 20 || value.Any(char.IsWhiteSpace))
        {
            return DomainError.Validation("Paste the whole token that claude setup-token printed (one line, no spaces).");
        }

        secrets.Store(ClaudeTokenSecret, value, by);
        await audit.RecordAsync([Change(ClaudeTokenSecret, "secret", "set", by)], cancellationToken).ConfigureAwait(false);
        return new SaveResult(RestartRequired: true);
    }

    /// <summary>Forgets the token: agents then use the server's own login.</summary>
    public async Task<SaveResult> RemoveClaudeTokenAsync(string by, CancellationToken cancellationToken)
    {
        if (secrets.Remove(ClaudeTokenSecret))
        {
            await audit.RecordAsync([Change(ClaudeTokenSecret, "secret", "removed", by)], cancellationToken).ConfigureAwait(false);
        }

        return new SaveResult(RestartRequired: true);
    }

    /// <summary>A test prompt with the given token, else the saved one, else the server's login. Nothing is saved.</summary>
    public Task<StepCheck> TestClaudeAsync(string? token, CancellationToken cancellationToken) =>
        claude.TestAsync(string.IsNullOrWhiteSpace(token) ? secrets.TryGet(ClaudeTokenSecret) : token.Trim(), cancellationToken);

    public ChatStep GetChat() => new(
        string.Equals(config.Read($"{Discord}:Enabled"), "true", StringComparison.OrdinalIgnoreCase),
        config.Read($"{Discord}:GuildId"),
        config.Read($"{Discord}:ChannelId"),
        secrets.Status(DiscordTokenSecret),
        [.. Users().Where(u => u.DiscordId is not null).Select(u => new ChatUser(u.Name, u.DiscordId!))]);

    public async Task<Result<SaveResult>> SaveChatAsync(ChatInput input, string by, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Enabled)
        {
            await config.SetAsync(new Dictionary<string, string?> { [$"{Discord}:Enabled"] = "false" }, cancellationToken).ConfigureAwait(false);
            await audit.RecordAsync([Change($"{Discord}:Enabled", "config", "set", by)], cancellationToken).ConfigureAwait(false);
            return new SaveResult(RestartRequired: true);
        }

        if (ValidateChat(input) is { } problem)
        {
            return DomainError.Validation(problem);
        }

        if (string.IsNullOrWhiteSpace(input.BotToken) && !secrets.Status(DiscordTokenSecret).Set)
        {
            return DomainError.Validation("Enter the bot token.");
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{Discord}:Enabled"] = "true",
            [$"{Discord}:GuildId"] = input.GuildId!.Trim(),
            [$"{Discord}:ChannelId"] = input.ChannelId!.Trim(),
        };
        if (!string.IsNullOrWhiteSpace(input.UserDiscordId))
        {
            AddUser(values, input.UserName!.Trim(), input.UserDiscordId.Trim());
        }

        await config.SetAsync(values, cancellationToken).ConfigureAwait(false);
        var changes = values.Keys.Select(k => Change(k, "config", "set", by)).ToList();
        if (!string.IsNullOrWhiteSpace(input.BotToken))
        {
            secrets.Store(DiscordTokenSecret, input.BotToken.Trim(), by);
            changes.Add(Change(DiscordTokenSecret, "secret", "set", by));
        }

        await audit.RecordAsync(changes, cancellationToken).ConfigureAwait(false);
        return new SaveResult(RestartRequired: true);
    }

    /// <summary>Posts a test message with the given settings (an empty token: the saved one). Nothing is saved.</summary>
    public async Task<StepCheck> TestChatAsync(ChatInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (ValidateChat(input with { UserName = null, UserDiscordId = null }) is { } problem)
        {
            return new StepCheck(false, problem, "fill in the server and channel IDs");
        }

        var token = string.IsNullOrWhiteSpace(input.BotToken) ? secrets.TryGet(DiscordTokenSecret) : input.BotToken.Trim();
        return token is null
            ? new StepCheck(false, "No bot token given or saved.", "Discord Developer Portal → your application → Bot → Reset Token")
            : await chat.TestAsync(new ChatConnection(token, input.GuildId!.Trim(), input.ChannelId!.Trim()), cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<RepositoryEntry> GetRepositories()
    {
        var entries = new List<RepositoryEntry>();
        for (var i = 0; config.Read($"Repositories:Items:{i}:Url") is { } url; i++)
        {
            var paths = new List<string>();
            for (var j = 0; config.Read($"Repositories:Items:{i}:MatchAreaPaths:{j}") is { } path; j++)
            {
                paths.Add(path);
            }

            entries.Add(new RepositoryEntry(url, config.Read($"Repositories:Items:{i}:Name"), config.Read($"Repositories:Items:{i}:BaseBranch"), config.Read($"Repositories:Items:{i}:MatchTag"), paths));
        }

        return entries;
    }

    /// <summary>
    /// Adds a repository to <c>agentd.json</c> after reaching it (the default branch is detected, as in <c>agentd repo add</c>).
    /// The daemon registers and clones it at its next start.
    /// </summary>
    public async Task<Result<RepositoryEntry>> AddRepositoryAsync(RepositoryInput input, string by, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var url = RemoteUrl.Parse(input.Url);
        if (!url.IsSuccess)
        {
            return url.Error;
        }

        var existing = GetRepositories();
        if (existing.Any(r => string.Equals(r.Url, url.Value.Value, StringComparison.OrdinalIgnoreCase)))
        {
            return DomainError.Conflict($"{url.Value} is already added.");
        }

        string baseBranch;
        try
        {
            baseBranch = string.IsNullOrWhiteSpace(input.BaseBranch)
                ? await remote.GetDefaultBranchAsync(url.Value.Value, cancellationToken).ConfigureAwait(false)
                : input.BaseBranch.Trim();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DomainError.Validation($"Cannot reach {url.Value}: {ex.Message}");
        }

        var name = string.IsNullOrWhiteSpace(input.Name) ? url.Value.AzureDevOps.Name : input.Name.Trim();
        var paths = (input.MatchAreaPaths ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var tag = string.IsNullOrWhiteSpace(input.MatchTag) ? (paths.Count == 0 ? $"repo:{name}" : null) : input.MatchTag.Trim();
        var at = $"Repositories:Items:{existing.Count}";
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{at}:Url"] = url.Value.Value,
            [$"{at}:Name"] = name,
            [$"{at}:BaseBranch"] = baseBranch,
        };
        if (tag is not null)
        {
            values[$"{at}:MatchTag"] = tag;
        }

        for (var i = 0; i < paths.Count; i++)
        {
            values[$"{at}:MatchAreaPaths:{i}"] = paths[i];
        }

        await config.SetAsync(values, cancellationToken).ConfigureAwait(false);
        await audit.RecordAsync([Change(at, "config", "added", by)], cancellationToken).ConfigureAwait(false);
        return new RepositoryEntry(url.Value.Value, name, baseBranch, tag, paths);
    }

    /// <summary>Reaches the repository with agentd's git setup and reports its default branch. Nothing is saved.</summary>
    public async Task<StepCheck> TestRepositoryAsync(string? url, CancellationToken cancellationToken)
    {
        var parsed = RemoteUrl.Parse(url);
        if (!parsed.IsSuccess)
        {
            return new StepCheck(false, parsed.Error.Message, "use the repository's Azure DevOps clone URL (SSH or HTTPS)");
        }

        try
        {
            var branch = await remote.GetDefaultBranchAsync(parsed.Value.Value, cancellationToken).ConfigureAwait(false);
            var repo = parsed.Value.AzureDevOps;
            return new StepCheck(true, $"Reached {repo.Organization}/{repo.Project}/{repo.Name}; its default branch is {branch}.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StepCheck(false, ex.Message, "check the Git access step: agentd's key must be added where the repository lives");
        }
    }

    /// <summary>
    /// Every step's own Test against the saved settings (not the running daemon's, which a restart replaces). Chat is
    /// only checked for completeness: a test message on every review would be noise.
    /// </summary>
    public async Task<IReadOnlyList<ReviewItem>> ReviewAsync(CancellationToken cancellationToken)
    {
        var items = new List<ReviewItem>
        {
            new("database", "Database", true, await TestDatabaseAsync(null, cancellationToken).ConfigureAwait(false)),
            new("azure-devops", "Azure DevOps", true, await TestAzureDevOpsAsync(null, cancellationToken).ConfigureAwait(false)),
            new("git", "Git access", false, gitKey.Read() is { } key
                ? new StepCheck(true, $"agentd's SSH key {key.Fingerprint}.")
                : new StepCheck(false, "agentd has no SSH key: git uses the SSH setup of the user it runs as.", "generate one in the Git access step, unless that setup already works")),
            new("claude", "Claude", true, await TestClaudeAsync(null, cancellationToken).ConfigureAwait(false)),
        };

        var chat = GetChat();
        items.Add(new("chat", "Chat", false, !chat.Enabled
            ? new StepCheck(true, "Off: everything happens in the web UI.")
            : chat.BotToken.Set && chat.Users.Count > 0
                ? new StepCheck(true, $"Discord, answering {string.Join(", ", chat.Users.Select(u => u.Name))}.")
                : new StepCheck(false, "Discord is on, but the bot token or a user with a Discord ID is missing.", "finish the Chat step")));

        var repositories = GetRepositories();
        if (repositories.Count == 0)
        {
            items.Add(new("repositories", "Repositories", false, new StepCheck(false, "No repositories yet.", "add one in the Repositories step, or later with agentd repo add")));
        }

        foreach (var repository in repositories)
        {
            items.Add(new("repositories", repository.Name ?? repository.Url, false, await TestRepositoryAsync(repository.Url, cancellationToken).ConfigureAwait(false)));
        }

        return items;
    }

    /// <summary>
    /// Finishes setup when every required check passes: <c>Setup:CompletedAt</c> goes into agentd.json and the
    /// one-time link is revoked, which also ends the setup session. Settings stay editable by an Admin.
    /// </summary>
    public async Task<Result<FinishResult>> FinishAsync(string by, CancellationToken cancellationToken)
    {
        var failing = (await ReviewAsync(cancellationToken).ConfigureAwait(false)).Where(i => i.Required && !i.Check.Ok).Select(i => i.Title).ToList();
        if (failing.Count > 0)
        {
            return DomainError.Validation($"Not finished yet: {string.Join(", ", failing)} must pass first.");
        }

        var at = time.GetUtcNow();
        await config.SetAsync(new Dictionary<string, string?> { ["Setup:CompletedAt"] = at.ToString("O", System.Globalization.CultureInfo.InvariantCulture) }, cancellationToken).ConfigureAwait(false);
        link.Revoke();
        await audit.RecordAsync([Change("Setup:CompletedAt", "config", "set", by)], cancellationToken).ConfigureAwait(false);
        return new FinishResult(at);
    }

    private static string? ValidateChat(ChatInput input)
    {
        if (!IsSnowflake(input.GuildId) || !IsSnowflake(input.ChannelId))
        {
            return "The server and channel IDs are long numbers (Discord: Developer Mode, then right-click → Copy ID).";
        }

        if (!string.IsNullOrWhiteSpace(input.UserDiscordId) && (!IsSnowflake(input.UserDiscordId) || string.IsNullOrWhiteSpace(input.UserName)))
        {
            return "Your Discord user ID is a long number, and agentd needs a name for you.";
        }

        return null;
    }

    private static bool IsSnowflake(string? value) => value?.Trim() is { Length: >= 15 and <= 22 } v && v.All(char.IsAsciiDigit);

    /// <summary>The users in agentd.json / the configuration, in order (<c>Users:0</c>, <c>Users:1</c>, …).</summary>
    private List<(string Name, string? DiscordId)> Users()
    {
        var users = new List<(string, string?)>();
        for (var i = 0; config.Read($"Users:{i}:Name") is { } name; i++)
        {
            users.Add((name, config.Read($"Users:{i}:Identities:Discord")));
        }

        return users;
    }

    /// <summary>Adds the Discord ID to the user with that name, or appends a new Admin.</summary>
    private void AddUser(Dictionary<string, string?> values, string name, string discordId)
    {
        var users = Users();
        var index = users.FindIndex(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = users.Count;
            values[$"Users:{index}:Name"] = name;
            values[$"Users:{index}:Roles:0"] = "Admin";
        }

        values[$"Users:{index}:Identities:Discord"] = discordId;
    }

    /// <summary>"https://dev.azure.com/myorg/", "https://myorg.visualstudio.com" or "myorg" → "myorg".</summary>
    public static string? OrganizationName(string? value)
    {
        var text = value?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var url))
        {
            return text.Contains('/', StringComparison.Ordinal) || text.Contains(' ', StringComparison.Ordinal) ? null : text;
        }

        if (url.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase))
        {
            return url.Host[..^".visualstudio.com".Length];
        }

        return string.Equals(url.Host, "dev.azure.com", StringComparison.OrdinalIgnoreCase) && url.Segments.Length >= 2
            ? Uri.UnescapeDataString(url.Segments[1].TrimEnd('/'))
            : null;
    }

    private static Result<AzureDevOpsConnection> Validate(AzureDevOpsInput input)
    {
        if (OrganizationName(input.Organization) is not { } organization)
        {
            return DomainError.Validation("Enter the organization: its name or URL (https://dev.azure.com/<name>).");
        }

        if (string.IsNullOrWhiteSpace(input.Project))
        {
            return DomainError.Validation("Enter the project name.");
        }

        var usePat = string.Equals(input.Auth, PatAuth, StringComparison.OrdinalIgnoreCase);
        if (!usePat && !string.Equals(input.Auth, AzCliAuth, StringComparison.OrdinalIgnoreCase))
        {
            return DomainError.Validation("Auth is Pat or AzCli.");
        }

        return new AzureDevOpsConnection(organization, input.Project.Trim(), usePat, null);
    }

    private SettingsChange Change(string key, string kind, string action, string by) => new(key, kind, action, by, time.GetUtcNow());
}
