using Npgsql;
using Testcontainers.PostgreSql;

namespace Agentd.Migrator.Tests.Schema;

/// <summary>One PostgreSQL container per test assembly; each test class creates its own database.</summary>
[TestClass]
public static class PostgresFixture
{
    private static PostgreSqlContainer? s_container;

    [AssemblyInitialize]
    public static async Task StartAsync(TestContext _)
    {
        s_container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await s_container.StartAsync().ConfigureAwait(false);
    }

    [AssemblyCleanup]
    public static async Task StopAsync()
    {
        if (s_container is not null)
        {
            await s_container.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Creates a fresh, empty database and returns its connection string.</summary>
    public static async Task<string> CreateDatabaseAsync(string name)
    {
        var admin = s_container?.GetConnectionString() ?? throw new InvalidOperationException("Container not started.");
        await using (var adminSource = NpgsqlDataSource.Create(admin))
        await using (var cmd = adminSource.CreateCommand($"CREATE DATABASE \"{name}\""))
        {
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }
}
