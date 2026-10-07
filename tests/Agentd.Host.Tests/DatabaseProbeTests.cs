using Agentd.Host.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Agentd.Host.Tests;

[TestClass]
[TestCategory("Integration")]
public sealed class DatabaseProbeTests
{
    private static PostgreSqlContainer? s_container;

    private readonly NpgsqlDatabaseProbe _probe = new(NullLoggerFactory.Instance);

    [ClassInitialize]
    public static async Task StartAsync(TestContext _)
    {
        s_container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await s_container.StartAsync();
    }

    [ClassCleanup]
    public static async Task StopAsync()
    {
        if (s_container is not null)
        {
            await s_container.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task A_new_database_is_tested_migrated_and_then_up_to_date()
    {
        var connectionString = await CreateDatabaseAsync("probe_fresh");

        var fresh = await _probe.TestAsync(connectionString, CancellationToken.None);
        var migrated = await _probe.MigrateAsync(connectionString, CancellationToken.None);
        var again = await _probe.MigrateAsync(connectionString, CancellationToken.None);
        var current = await _probe.TestAsync(connectionString, CancellationToken.None);

        Assert.IsTrue(fresh.Ok, fresh.Message);
        Assert.Contains("isn't created yet", fresh.Message);
        Assert.IsTrue(migrated.Ok, migrated.Message);
        Assert.Contains("Applied", migrated.Message);
        Assert.Contains("already up to date", again.Message);
        Assert.IsTrue(current.Ok);
        Assert.Contains("up to date", current.Message);
        Assert.IsNull(current.Fix);
    }

    [TestMethod]
    public async Task A_wrong_password_fails_without_echoing_it()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(s_container!.GetConnectionString()) { Password = "not-the-pw-SECRET" }.ConnectionString;

        var check = await _probe.TestAsync(connectionString, CancellationToken.None);

        Assert.IsFalse(check.Ok);
        Assert.DoesNotContain("SECRET", check.Message);
        Assert.IsNotNull(check.Fix);
    }

    [TestMethod]
    [DataRow("garbage", false)]
    [DataRow("Username=x", false)]
    [DataRow("Host=db;Username=x;Password=y;Database=z", true)]
    public void Problem_spots_strings_that_arent_connection_strings(string value, bool valid) =>
        Assert.AreEqual(valid, _probe.Problem(value) is null);

    private static async Task<string> CreateDatabaseAsync(string name)
    {
        var admin = s_container!.GetConnectionString();
        await using (var source = NpgsqlDataSource.Create(admin))
        await using (var command = source.CreateCommand($"CREATE DATABASE \"{name}\""))
        {
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
    }
}
