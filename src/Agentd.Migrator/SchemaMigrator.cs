using Agentd.Migrator.Migrations;
using FluentMigrator.Runner;
using FluentMigrator.Runner.VersionTableInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentd.Migrator;

/// <summary>
/// Runs FluentMigrator (versioned migrations, then the <see cref="Routines.ApplyRoutines"/> maintenance step)
/// under a PostgreSQL advisory lock, so concurrent migrators never run at the same time.
/// </summary>
public sealed partial class SchemaMigrator(string connectionString, ILoggerFactory loggerFactory)
{
    private const string LockSql = "SELECT pg_advisory_lock(hashtext('agentd.migrate'))";
    private const string UnlockSql = "SELECT pg_advisory_unlock(hashtext('agentd.migrate'))";

    private readonly ILogger _logger = loggerFactory.CreateLogger<SchemaMigrator>();

    public async Task<MigrationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        // The session-level advisory lock is held on this dedicated connection for the whole run.
        await using var lockConnection = new NpgsqlConnection(connectionString);
        await lockConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(lockConnection, LockSql, cancellationToken).ConfigureAwait(false);
        try
        {
            var before = await ReadAppliedVersionsAsync(lockConnection, cancellationToken).ConfigureAwait(false);

            await using (var services = BuildServices())
            {
                using var scope = services.CreateScope();
                cancellationToken.ThrowIfCancellationRequested();
                scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
            }

            var after = await ReadAppliedVersionsAsync(lockConnection, cancellationToken).ConfigureAwait(false);
            var applied = after.Except(before).Order().ToList();
            LogCompleted(_logger, applied.Count, after.Count);
            return new MigrationResult(applied);
        }
        finally
        {
            await ExecuteAsync(lockConnection, UnlockSql, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private ServiceProvider BuildServices() =>
        new ServiceCollection()
            .AddSingleton(loggerFactory)
            .AddLogging()
            .AddFluentMigratorCore()
            .ConfigureRunner(runner => runner
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .ScanIn(typeof(SchemaMigrator).Assembly).For.All())   // All = migrations + [Maintenance] (routines)
            .AddScoped<IVersionTableMetaData, AgentdVersionTable>()
            .BuildServiceProvider(validateScopes: true);

    private static async Task<List<long>> ReadAppliedVersionsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var exists = new NpgsqlCommand("SELECT to_regclass('agentd.schema_version') IS NOT NULL", connection);
        if (!(bool)(await exists.ExecuteScalarAsync(ct).ConfigureAwait(false))!)
        {
            return [];
        }

        var versions = new List<long>();
        await using var cmd = new NpgsqlCommand("SELECT version FROM agentd.schema_version", connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            versions.Add(reader.GetInt64(0));
        }

        return versions;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Schema migration completed: {Applied} migration(s) applied, {Total} in total; routines re-applied")]
    private static partial void LogCompleted(ILogger logger, int applied, int total);
}

/// <summary>Versions of the migrations applied by this run.</summary>
public sealed record MigrationResult(IReadOnlyList<long> AppliedVersions);
