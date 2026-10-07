using Agentd.Application.Setup;
using Agentd.Migrator;
using Npgsql;

namespace Agentd.Host.Setup;

/// <summary>The database step's Test and Migrate, against a connection string that may not be saved yet.</summary>
internal sealed class NpgsqlDatabaseProbe(ILoggerFactory loggers) : IDatabaseProbe
{
    private const string Fix = "check the host, port, user name and password, and that PostgreSQL is running";

    public string? Problem(string connectionString)
    {
        try
        {
            return string.IsNullOrWhiteSpace(new NpgsqlConnectionStringBuilder(connectionString).Host)
                ? "The connection string has no Host."
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or KeyNotFoundException)
        {
            return "That isn't a PostgreSQL connection string (Host=…;Port=5432;Username=…;Password=…;Database=…).";
        }
    }

    public async Task<StepCheck> TestAsync(string connectionString, CancellationToken cancellationToken)
    {
        var quick = new NpgsqlConnectionStringBuilder(connectionString) { Timeout = 5, Pooling = false }.ConnectionString;
        try
        {
            await using var connection = new NpgsqlConnection(quick);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT to_regclass('agentd.jobs') IS NOT NULL", connection);
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!)
            {
                return new StepCheck(true, $"Connected to PostgreSQL {connection.PostgreSqlVersion}. The agentd schema isn't created yet.", "run the migrations");
            }

            var pending = await new SchemaMigrator(quick, loggers).PendingAsync(cancellationToken).ConfigureAwait(false);
            return pending.Count == 0
                ? new StepCheck(true, $"Connected to PostgreSQL {connection.PostgreSqlVersion}. The schema is up to date.")
                : new StepCheck(true, $"Connected to PostgreSQL {connection.PostgreSqlVersion}. {pending.Count} migration(s) to apply.", "run the migrations");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return new StepCheck(false, ex.Message, Fix);
        }
    }

    public async Task<StepCheck> MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            var result = await new SchemaMigrator(connectionString, loggers).MigrateAsync(cancellationToken).ConfigureAwait(false);
            return new StepCheck(true, result.AppliedVersions.Count == 0
                ? "The schema was already up to date."
                : $"Applied {result.AppliedVersions.Count} migration(s); the schema is up to date.");
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return new StepCheck(false, ex.Message, Fix);
        }
    }
}
