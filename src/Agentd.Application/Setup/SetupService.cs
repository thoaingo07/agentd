using Agentd.Domain.Common;

namespace Agentd.Application.Setup;

/// <summary>The database step: the connection string is a secret, so only its status is shown.</summary>
public sealed record DatabaseStep(SecretStatus ConnectionString);

/// <summary>A saved step. The daemon reads these settings at start, so they apply after a restart.</summary>
public sealed record SaveResult(bool RestartRequired);

/// <summary>
/// The setup and settings use cases, shared by the web wizard, Settings and <c>agentd init</c>
/// (docs/architect/deployment.md §5a). Each step has a Save and a Test; secrets are write-only and
/// every change is audited without its value.
/// </summary>
public sealed class SetupService(
    ISecrets secrets,
    ISettingsAudit audit,
    IDatabaseProbe database,
    TimeProvider time)
{
    public const string ConnectionStringSecret = "ConnectionStrings:agentd";

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

    private SettingsChange Change(string key, string kind, string action, string by) => new(key, kind, action, by, time.GetUtcNow());
}
