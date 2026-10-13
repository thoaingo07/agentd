using Agentd.Application.Prompts;
using Agentd.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class PromptLogStoreTests
{
    private static PromptStart Prompt(string rules, string text = "Fix the login", long sourceId = 42) =>
        new(PromptSources.Job, sourceId, "implement", Guid.NewGuid(), false, "opus", "high", null, rules, text);

    [TestMethod]
    public async Task A_prompt_is_kept_with_its_rules_once_and_finishes_once()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_round_trip");
        var log = new PromptLogStore(db);

        var first = await log.StartAsync(Prompt("the job rules"), default);
        var second = await log.StartAsync(Prompt("the job rules", "Address the review") with { Source = PromptSources.ReviewAsk, Step = null, Rules = null }, default);
        await log.FinishAsync(first, PromptOutcomes.Ok, null, null, default);
        await log.FinishAsync(first, PromptOutcomes.Error, "late", "late", default);   // a second finish changes nothing
        await log.FinishAsync(second, PromptOutcomes.UsageLimit, null, "partial", default);

        var rows = await RowsAsync(db);
        Assert.AreEqual(("job", 42L, "implement", "opus", "high", "Fix the login", "ok", (string?)null), (rows[0].Source, rows[0].SourceId, rows[0].Step, rows[0].Model, rows[0].Effort, rows[0].Prompt, rows[0].Outcome, rows[0].Error));
        Assert.AreEqual("the job rules", rows[0].Rules);
        Assert.AreEqual(("review-ask", (string?)null, "usage-limit", "partial"), (rows[1].Source, rows[1].Rules, rows[1].Outcome, rows[1].Reply));
        Assert.IsTrue(rows.All(r => r.Finished));
    }

    [TestMethod]
    public async Task Parallel_prompts_with_the_same_rules_share_one_rules_row()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_parallel");
        var log = new PromptLogStore(db);

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => log.StartAsync(Prompt(i % 2 == 0 ? "rules A" : "rules B", $"prompt {i}", i), default))));

        Assert.AreEqual(8, ids.Distinct().Count());
        Assert.AreEqual(2L, await ScalarAsync<long>(db, "SELECT count(*) FROM agentd.agent_prompt_rules"));
        Assert.AreEqual(8L, await ScalarAsync<long>(db, "SELECT count(*) FROM agentd.agent_prompts"));
    }

    [TestMethod]
    public async Task Old_prompts_go_and_rules_go_with_them_unless_used_since()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_retention");
        var log = new PromptLogStore(db);
        var old = await log.StartAsync(Prompt("old rules"), default);
        var oldShared = await log.StartAsync(Prompt("shared rules"), default);
        await ExecuteAsync(db, $"UPDATE agentd.agent_prompts SET started_at = now() - interval '100 days' WHERE id IN ({old}, {oldShared})");
        await ExecuteAsync(db, "UPDATE agentd.agent_prompt_rules SET last_used = now() - interval '100 days'");
        await log.StartAsync(Prompt("shared rules"), default);   // used again today

        var deleted = await log.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddDays(-90), default);
        var again = await log.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddDays(-90), default);

        Assert.AreEqual((2, 0), (deleted, again));
        Assert.AreEqual(1L, await ScalarAsync<long>(db, "SELECT count(*) FROM agentd.agent_prompts"));
        Assert.AreEqual("shared rules", await ScalarAsync<string>(db, "SELECT text FROM agentd.agent_prompt_rules"));
    }

    private sealed record Row(string Source, long SourceId, string? Step, string? Model, string? Effort, string Prompt, string? Rules, string? Outcome, string? Error, string? Reply, bool Finished);

    private static async Task<List<Row>> RowsAsync(NpgsqlDataSource db)
    {
        await using var cmd = db.CreateCommand("SELECT p.source, p.source_id, p.step, p.model, p.effort, p.prompt, r.text, p.outcome, p.error, p.reply, p.finished_at IS NOT NULL " +
            "FROM agentd.agent_prompts p LEFT JOIN agentd.agent_prompt_rules r ON r.hash = p.rules_hash ORDER BY p.id");
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<Row>();
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (await r.ReadAsync())
        {
            rows.Add(new Row(r.GetString(0), r.GetInt64(1), S(2), S(3), S(4), r.GetString(5), S(6), S(7), S(8), S(9), r.GetBoolean(10)));
        }

        return rows;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync();
    }
}
