using Agentd.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class DatabaseMigratorTests
{
    [TestMethod]
    public async Task First_run_applies_embedded_migrations_and_routines()
    {
        await using var db = await PostgresFixture.CreateDatabaseAsync("migrator_first_run");
        var result = await NewMigrator(db).MigrateAsync(CancellationToken.None);

        CollectionAssert.Contains(result.Migrations.ToList(), "Migrations/0001_initial.sql");
        CollectionAssert.Contains(result.Routines.ToList(), "Routines/job/job_create.sql");
        CollectionAssert.Contains(result.Routines.ToList(), "Routines/event/event_append.sql");
        Assert.IsTrue(await TableExistsAsync(db, "jobs"));
        Assert.IsTrue(await TableExistsAsync(db, "events"));
    }

    [TestMethod]
    public async Task Second_run_is_a_no_op()
    {
        await using var db = await PostgresFixture.CreateDatabaseAsync("migrator_second_run");
        var migrator = NewMigrator(db);
        await migrator.MigrateAsync(CancellationToken.None);

        var second = await migrator.MigrateAsync(CancellationToken.None);

        Assert.IsEmpty(second.Migrations);
        Assert.IsEmpty(second.Routines);
    }

    [TestMethod]
    public async Task Modified_applied_migration_fails_fast()
    {
        await using var db = await PostgresFixture.CreateDatabaseAsync("migrator_checksum");
        var migrator = NewMigrator(db);
        var original = SqlScript.Create("Migrations/9001_test.sql", "CREATE TABLE agentd.t9001 (id int);");
        await migrator.MigrateAsync([original], [], CancellationToken.None);

        var modified = SqlScript.Create("Migrations/9001_test.sql", "CREATE TABLE agentd.t9001 (id bigint);");

        await Assert.ThrowsExactlyAsync<MigrationChecksumMismatchException>(
            () => migrator.MigrateAsync([modified], [], CancellationToken.None));
    }

    [TestMethod]
    public async Task Changed_routine_is_reapplied()
    {
        await using var db = await PostgresFixture.CreateDatabaseAsync("migrator_routine_change");
        var migrator = NewMigrator(db);
        var v1 = SqlScript.Create("Routines/test/answer.sql", "CREATE OR REPLACE FUNCTION agentd.answer() RETURNS int LANGUAGE sql AS $$ SELECT 41 $$;");
        var v2 = SqlScript.Create("Routines/test/answer.sql", "CREATE OR REPLACE FUNCTION agentd.answer() RETURNS int LANGUAGE sql AS $$ SELECT 42 $$;");

        await migrator.MigrateAsync([], [v1], CancellationToken.None);
        var result = await migrator.MigrateAsync([], [v2], CancellationToken.None);

        Assert.AreEqual("Routines/test/answer.sql", result.Routines.Single());
        await using var cmd = db.CreateCommand("SELECT agentd.answer()");
        Assert.AreEqual(42, (int)(await cmd.ExecuteScalarAsync())!);
    }

    [TestMethod]
    public async Task Concurrent_migrators_apply_each_script_once()
    {
        await using var db = await PostgresFixture.CreateDatabaseAsync("migrator_concurrent");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => NewMigrator(db).MigrateAsync(CancellationToken.None)));

        Assert.AreEqual(1, results.Count(r => r.Migrations.Count > 0), "exactly one migrator should apply the migrations");
        await using var cmd = db.CreateCommand("SELECT count(*) FROM agentd.schema_migrations");
        Assert.AreEqual(1L, (long)(await cmd.ExecuteScalarAsync())!);
    }

    private static DatabaseMigrator NewMigrator(NpgsqlDataSource db) => new(db, NullLogger<DatabaseMigrator>.Instance);

    private static async Task<bool> TableExistsAsync(NpgsqlDataSource db, string table)
    {
        await using var cmd = db.CreateCommand("SELECT to_regclass('agentd.' || $1) IS NOT NULL");
        cmd.Parameters.Add(new NpgsqlParameter { Value = table });
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
}
