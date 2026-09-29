using Microsoft.Extensions.Logging;
using Npgsql;

namespace Agentd.Infrastructure.Persistence.Migrations;

/// <summary>
/// Applies versioned SQL migrations (once, in order) and repeatable routines (when new or changed),
/// under a PostgreSQL advisory lock so concurrent instances never migrate at the same time.
/// See docs/architect/data-access.md §4.
/// </summary>
public sealed partial class DatabaseMigrator(NpgsqlDataSource dataSource, ILogger<DatabaseMigrator> logger)
{
    private const string Bootstrap = """
        CREATE SCHEMA IF NOT EXISTS agentd;
        CREATE TABLE IF NOT EXISTS agentd.schema_migrations (
            version    text PRIMARY KEY,
            checksum   text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        );
        CREATE TABLE IF NOT EXISTS agentd.schema_routines (
            id         text PRIMARY KEY,
            checksum   text NOT NULL,
            applied_at timestamptz NOT NULL DEFAULT now()
        );
        """;

    public Task<MigrationResult> MigrateAsync(CancellationToken cancellationToken) =>
        MigrateAsync(SqlScript.LoadMigrations(), SqlScript.LoadRoutines(), cancellationToken);

    internal async Task<MigrationResult> MigrateAsync(
        IReadOnlyList<SqlScript> migrations,
        IReadOnlyList<SqlScript> routines,
        CancellationToken cancellationToken)
    {
        // One dedicated connection holds the session-level advisory lock for the whole run.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, null, "SELECT pg_advisory_lock(hashtext('agentd.migrate'))", cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, null, Bootstrap, cancellationToken).ConfigureAwait(false);

            var appliedMigrations = await ApplyMigrationsAsync(connection, migrations, cancellationToken).ConfigureAwait(false);
            var appliedRoutines = await ApplyRoutinesAsync(connection, routines, cancellationToken).ConfigureAwait(false);

            LogCompleted(logger, appliedMigrations.Count, appliedRoutines.Count);
            return new MigrationResult(appliedMigrations, appliedRoutines);
        }
        finally
        {
            await ExecuteAsync(connection, null, "SELECT pg_advisory_unlock(hashtext('agentd.migrate'))", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<List<string>> ApplyMigrationsAsync(
        NpgsqlConnection connection, IReadOnlyList<SqlScript> migrations, CancellationToken ct)
    {
        var recorded = await ReadChecksumsAsync(connection, "SELECT version, checksum FROM agentd.schema_migrations", ct).ConfigureAwait(false);
        var applied = new List<string>();

        foreach (var script in migrations.OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (recorded.TryGetValue(script.Id, out var checksum))
            {
                if (checksum != script.Checksum)
                {
                    throw new MigrationChecksumMismatchException(
                        $"Migration '{script.Id}' was modified after it was applied. Applied migrations are immutable; add a new migration instead.");
                }

                continue;
            }

            await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await ExecuteAsync(connection, tx, script.Sql, ct).ConfigureAwait(false);
            await RecordAsync(connection, tx, "INSERT INTO agentd.schema_migrations (version, checksum) VALUES ($1, $2)", script, ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            LogApplied(logger, "migration", script.Id);
            applied.Add(script.Id);
        }

        return applied;
    }

    private async Task<List<string>> ApplyRoutinesAsync(
        NpgsqlConnection connection, IReadOnlyList<SqlScript> routines, CancellationToken ct)
    {
        var recorded = await ReadChecksumsAsync(connection, "SELECT id, checksum FROM agentd.schema_routines", ct).ConfigureAwait(false);
        var changed = routines
            .Where(r => !recorded.TryGetValue(r.Id, out var checksum) || checksum != r.Checksum)
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToList();

        if (changed.Count == 0)
        {
            return [];
        }

        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var routine in changed)
        {
            await ExecuteAsync(connection, tx, routine.Sql, ct).ConfigureAwait(false);
            await RecordAsync(
                connection,
                tx,
                """
                INSERT INTO agentd.schema_routines (id, checksum) VALUES ($1, $2)
                ON CONFLICT (id) DO UPDATE SET checksum = EXCLUDED.checksum, applied_at = now()
                """,
                routine,
                ct).ConfigureAwait(false);
            LogApplied(logger, "routine", routine.Id);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return changed.Select(r => r.Id).ToList();
    }

    private static async Task<Dictionary<string, string>> ReadChecksumsAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var cmd = new NpgsqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static async Task RecordAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, SqlScript script, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = script.Id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = script.Checksum });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, connection, tx);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Applied {Kind} {Id}")]
    private static partial void LogApplied(ILogger logger, string kind, string id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database migration completed: {Migrations} migration(s), {Routines} routine(s) applied")]
    private static partial void LogCompleted(ILogger logger, int migrations, int routines);
}

/// <summary>What a migration run applied.</summary>
public sealed record MigrationResult(IReadOnlyList<string> Migrations, IReadOnlyList<string> Routines);
