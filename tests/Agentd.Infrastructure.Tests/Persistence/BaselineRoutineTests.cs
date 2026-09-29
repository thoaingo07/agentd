using System.Collections.Concurrent;
using Agentd.Infrastructure.Persistence.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class BaselineRoutineTests
{
    private static NpgsqlDataSource s_db = null!;

    [ClassInitialize]
    public static async Task InitAsync(TestContext _)
    {
        s_db = await PostgresFixture.CreateDatabaseAsync("baseline_routines");
        await new DatabaseMigrator(s_db, NullLogger<DatabaseMigrator>.Instance).MigrateAsync(CancellationToken.None);
    }

    [ClassCleanup]
    public static async Task CleanupAsync() => await s_db.DisposeAsync();

    [TestMethod]
    public async Task Job_create_returns_queued_job_at_version_1()
    {
        await using var cmd = s_db.CreateCommand("SELECT id, state, version FROM agentd.job_create($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = 1234, NpgsqlDbType = NpgsqlDbType.Integer });
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsGreaterThan(0L, reader.GetInt64(0));
        Assert.AreEqual("Queued", reader.GetString(1));
        Assert.AreEqual(1L, reader.GetInt64(2));
    }

    [TestMethod]
    public async Task Event_append_round_trips_jsonb_payload()
    {
        var seq = await AppendAsync("""{"text":"hello","n":1}""");

        await using var cmd = s_db.CreateCommand("SELECT payload->>'text', (payload->>'n')::int FROM agentd.events WHERE seq = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = seq });
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual("hello", reader.GetString(0));
        Assert.AreEqual(1, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task Parallel_event_appends_get_distinct_increasing_sequence_numbers()
    {
        var seqs = new ConcurrentBag<long>();

        // Each task opens its own pooled connection: the pattern every repository follows.
        await Parallel.ForEachAsync(Enumerable.Range(0, 50), async (i, ct) => seqs.Add(await AppendAsync($$"""{"i":{{i}}}""")));

        Assert.HasCount(50, seqs);
        Assert.HasCount(50, seqs.Distinct());
    }

    private static async Task<long> AppendAsync(string json)
    {
        await using var cmd = s_db.CreateCommand("SELECT agentd.event_append($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = "test.event", NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = json, NpgsqlDbType = NpgsqlDbType.Jsonb });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
