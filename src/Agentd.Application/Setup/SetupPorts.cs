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
