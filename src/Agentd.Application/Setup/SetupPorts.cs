namespace Agentd.Application.Setup;

/// <summary><c>agentd.json</c>: keys relative to the <c>Agentd</c> section (e.g. <c>AzureDevOps:Project</c>).</summary>
public interface IConfigWriter
{
    /// <summary>The value in <c>agentd.json</c>, else the running configuration's.</summary>
    string? Read(string key);

    /// <summary>Sets (or, for null, removes) the keys in one atomic write.</summary>
    Task SetAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken);
}

/// <summary>A secret's metadata: whether it's set, when and by whom. Never the value.</summary>
public sealed record SecretStatus(bool Set, DateTimeOffset? UpdatedAt = null, string? UpdatedBy = null)
{
    public static SecretStatus Missing { get; } = new(false);
}

/// <summary>The encrypted secret store. Values are only read server-side (to test a connection), never returned.</summary>
public interface ISecrets
{
    SecretStatus Status(string key);

    string? TryGet(string key);

    void Store(string key, string value, string by);

    bool Remove(string key);
}

/// <summary>One audited change: which key, by whom, when. Never the value.</summary>
public sealed record SettingsChange(string Key, string Kind, string Action, string By, DateTimeOffset At);

/// <summary>The <c>settings.changed</c> trail. It works before the database exists (setup configures it).</summary>
public interface ISettingsAudit
{
    Task RecordAsync(IReadOnlyList<SettingsChange> changes, CancellationToken cancellationToken);
}

/// <summary>A step's "Test" result, with a one-line fix when it failed.</summary>
public sealed record StepCheck(bool Ok, string Message, string? Fix = null);

public interface IDatabaseProbe
{
    /// <summary>Why <paramref name="connectionString"/> isn't a PostgreSQL connection string, or null.</summary>
    string? Problem(string connectionString);

    /// <summary>Connects, then reports the schema: missing, out of date, or current.</summary>
    Task<StepCheck> TestAsync(string connectionString, CancellationToken cancellationToken);

    Task<StepCheck> MigrateAsync(string connectionString, CancellationToken cancellationToken);
}

/// <param name="Organization">The organization name (e.g. <c>myorg</c>).</param>
/// <param name="Project">The project name.</param>
/// <param name="UsePat">A personal access token; otherwise the server's <c>az login</c>.</param>
/// <param name="Pat">The token, when <paramref name="UsePat"/>.</param>
public sealed record AzureDevOpsConnection(string Organization, string Project, bool UsePat, string? Pat);

public interface IAzureDevOpsProbe
{
    /// <summary>The daemon's own WIQL query (work items tagged and waiting, per <paramref name="jobs"/>).</summary>
    Task<StepCheck> TestAsync(AzureDevOpsConnection connection, Jobs.JobOptions jobs, CancellationToken cancellationToken);
}
