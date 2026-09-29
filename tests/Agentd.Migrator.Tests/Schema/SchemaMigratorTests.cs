using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Agentd.Migrator.Tests.Schema;

[TestClass]
[TestCategory("Integration")]
public sealed class SchemaMigratorTests
{
    private const long InitialSchema = 2026_09_29_0001;

    [TestMethod]
    public async Task First_run_applies_sql_migrations_and_routines()
    {
        var cs = await PostgresFixture.CreateDatabaseAsync("fm_first_run");

        var result = await Migrate(cs);

        CollectionAssert.Contains(result.AppliedVersions.ToList(), InitialSchema);
        CollectionAssert.Contains(result.AppliedVersions.ToList(), 2026_09_30_0001L);
        await using var db = NpgsqlDataSource.Create(cs);
        Assert.IsTrue(await ScalarAsync<bool>(db, "SELECT to_regclass('agentd.jobs') IS NOT NULL"));
        Assert.IsTrue(await ScalarAsync<bool>(db, "SELECT to_regclass('agentd.events') IS NOT NULL"));
        Assert.IsTrue(await ScalarAsync<bool>(db, "SELECT to_regclass('agentd.schema_version') IS NOT NULL"), "version table lives in the agentd schema");
        Assert.IsTrue(await FunctionExistsAsync(db, "job_insert"));
        Assert.IsTrue(await FunctionExistsAsync(db, "event_append"));
    }

    [TestMethod]
    public async Task Second_run_applies_no_migrations()
    {
        var cs = await PostgresFixture.CreateDatabaseAsync("fm_second_run");
        await Migrate(cs);

        var second = await Migrate(cs);

        Assert.IsEmpty(second.AppliedVersions);
    }

    [TestMethod]
    public async Task Routines_are_reapplied_on_every_run()
    {
        var cs = await PostgresFixture.CreateDatabaseAsync("fm_routines");
        await Migrate(cs);
        await using var db = NpgsqlDataSource.Create(cs);
        await using (var drop = db.CreateCommand("DROP FUNCTION agentd.job_dequeue(text, timestamptz)"))
        {
            await drop.ExecuteNonQueryAsync();
        }

        await Migrate(cs);

        Assert.IsTrue(await FunctionExistsAsync(db, "job_insert"));
    }

    [TestMethod]
    public async Task Concurrent_migrators_apply_each_migration_once()
    {
        var cs = await PostgresFixture.CreateDatabaseAsync("fm_concurrent");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Migrate(cs)));

        Assert.AreEqual(1, results.Count(r => r.AppliedVersions.Contains(InitialSchema)), "exactly one migrator applies the initial schema");
        await using var db = NpgsqlDataSource.Create(cs);
        Assert.AreEqual(1L, await ScalarAsync<long>(db, $"SELECT count(*) FROM agentd.schema_version WHERE version = {InitialSchema}"));
    }

    [TestMethod]
    public void Every_migration_class_has_an_up_script()
    {
        var migrations = typeof(SchemaMigrator).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && t.IsSubclassOf(typeof(Migrations.SqlMigration)))
            .ToList();

        Assert.IsNotEmpty(migrations);
        foreach (var type in migrations)
        {
            var version = ((FluentMigrator.MigrationAttribute)Attribute.GetCustomAttribute(type, typeof(FluentMigrator.MigrationAttribute))!).Version;
            Assert.IsTrue(
                SqlResources.List("Migrations").Any(p => p.StartsWith($"Migrations/{version}_", StringComparison.Ordinal) && p.EndsWith(".up.sql", StringComparison.Ordinal)),
                $"{type.Name} ({version}) has no Migrations/{version}_*.up.sql");
        }
    }

    private static Task<MigrationResult> Migrate(string connectionString) =>
        new SchemaMigrator(connectionString, NullLoggerFactory.Instance).MigrateAsync(CancellationToken.None);

    private static async Task<bool> FunctionExistsAsync(NpgsqlDataSource db, string name) =>
        await ScalarAsync<bool>(db, $"SELECT EXISTS (SELECT 1 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'agentd' AND p.proname = '{name}')");

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
