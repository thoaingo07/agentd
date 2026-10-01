using Agentd.Migrator;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Agentd.Infrastructure.Tests.Persistence;

/// <summary>One PostgreSQL container per assembly; each test class gets a freshly migrated database.</summary>
[TestClass]
public static class Database
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

    public static async Task<NpgsqlDataSource> CreateMigratedAsync(string name)
    {
        var admin = s_container?.GetConnectionString() ?? throw new InvalidOperationException("Container not started.");
        await using (var adminSource = NpgsqlDataSource.Create(admin))
        await using (var cmd = adminSource.CreateCommand($"CREATE DATABASE \"{name}\""))
        {
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var cs = new NpgsqlConnectionStringBuilder(admin) { Database = name, MaxPoolSize = 50 }.ConnectionString;
        await new SchemaMigrator(cs, NullLoggerFactory.Instance).MigrateAsync(CancellationToken.None).ConfigureAwait(false);
        return NpgsqlDataSource.Create(cs);
    }
}
