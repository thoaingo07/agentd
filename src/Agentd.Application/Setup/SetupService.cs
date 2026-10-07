using Agentd.Application.Jobs;
using Agentd.Domain.Common;
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
    IOptions<JobOptions> jobs,
    TimeProvider time)
{
    public const string ConnectionStringSecret = "ConnectionStrings:agentd";
    public const string PatSecret = "AzureDevOps:Pat";
    public const string ClaudeTokenSecret = "Claude:OAuthToken";
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
