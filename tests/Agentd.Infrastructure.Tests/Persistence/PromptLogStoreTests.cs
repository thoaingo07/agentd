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
    public async Task The_list_pages_newest_first_with_filters_and_a_prompt_comes_whole()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_list");
        var log = new PromptLogStore(db);
        var job = await log.StartAsync(Prompt("the job rules", new string('x', 400)), default);
        var running = await log.StartAsync(Prompt("the job rules", "Address the review"), default);
        var ask = await log.StartAsync(Prompt("ask rules", "why a loop?", 7) with { Source = PromptSources.ReviewAsk, Step = null, Model = "sonnet" }, default);
        await log.FinishAsync(job, PromptOutcomes.Ok, null, "Done.", default);
        await log.FinishAsync(ask, PromptOutcomes.Error, "exit 1", null, default);

        var all = await log.ListAsync(new PromptFilter(), null, 10, default);
        var page = await log.ListAsync(new PromptFilter(), ask, 1, default);
        var jobs = await log.ListAsync(new PromptFilter(Source: PromptSources.Job, SourceId: 42, Step: "implement", Model: "opus"), null, 10, default);
        var stillRunning = await log.ListAsync(new PromptFilter(Outcome: PromptFilter.Running), null, 10, default);
        var failed = await log.ListAsync(new PromptFilter(Outcome: PromptOutcomes.Error), null, 10, default);
        var later = await log.ListAsync(new PromptFilter(From: DateTimeOffset.UtcNow.AddHours(1)), null, 10, default);

        CollectionAssert.AreEqual(new[] { ask, running, job }, all.Select(p => p.Id).ToList(), "newest first");
        CollectionAssert.AreEqual(new[] { running }, page.Select(p => p.Id).ToList(), "below the given id");
        CollectionAssert.AreEqual(new[] { running, job }, jobs.Select(p => p.Id).ToList());
        CollectionAssert.AreEqual(new[] { running }, stillRunning.Select(p => p.Id).ToList());
        Assert.AreEqual(("exit 1", "sonnet"), (failed.Single().Error, failed.Single().Model));
        Assert.IsEmpty(later);
        var first = all.Single(p => p.Id == job);
        Assert.AreEqual((300, 400, 5, true, (string?)PromptOutcomes.Ok), (first.Preview.Length, first.PromptChars, first.ReplyChars, first.HasRules, first.Outcome));

        var whole = await log.GetAsync(job, default);
        Assert.AreEqual((400, "Done.", "the job rules", (string?)"implement"), (whole!.Prompt.Length, whole.Reply, whole.Rules, whole.Step));
        Assert.IsNotNull(whole.FinishedAt);
        Assert.IsNull(await log.GetAsync(999, default));
    }

    [TestMethod]
    public async Task Readers_in_parallel_with_writers_see_whole_prompts()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_parallel_read");
        var log = new PromptLogStore(db);
        var seed = await log.StartAsync(Prompt("rules"), default);

        var writes = Enumerable.Range(0, 6).Select(i => Task.Run(() => log.StartAsync(Prompt("rules", $"prompt {i}", i), default)));
        var reads = Enumerable.Range(0, 6).Select(_ => Task.Run(async () => (await log.ListAsync(new PromptFilter(), null, 50, default)).Count));
        var gets = Enumerable.Range(0, 6).Select(_ => Task.Run(() => log.GetAsync(seed, default)));
        await Task.WhenAll(writes);
        var counts = await Task.WhenAll(reads);
        var got = await Task.WhenAll(gets);

        Assert.IsTrue(counts.All(c => c is >= 1 and <= 7));
        Assert.IsTrue(got.All(p => p is { Rules: "rules", Prompt: "Fix the login" }));
        Assert.HasCount(7, await log.ListAsync(new PromptFilter(), null, 50, default));
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

    [TestMethod]
    public async Task A_thread_turns_transcript_is_kept_in_order_and_goes_with_its_prompt()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_transcript");
        var log = new PromptLogStore(db);
        var id = await log.StartAsync(Prompt("rules") with { Source = PromptSources.Idea, Step = null }, default);
        var at = DateTimeOffset.UtcNow;

        await log.AddEventsAsync(id, [
            new PromptEvent("agent.text", "{\"text\":\"Reading the repo.\"}", at),
            new PromptEvent("agent.tool_call", "{\"id\":\"t1\",\"name\":\"Read\",\"input\":{\"file_path\":\"README.md\"}}", at.AddSeconds(1)),
            new PromptEvent("agent.tool_result", "{\"toolUseId\":\"t1\",\"content\":\"# agentd\"}", at.AddSeconds(2)),
        ], default);
        await log.AddEventsAsync(id + 999, [new PromptEvent("agent.text", "{}", at)], default);   // no such prompt: nothing kept

        var transcript = await log.TranscriptAsync(id, 100, default);
        CollectionAssert.AreEqual(new[] { "agent.text", "agent.tool_call", "agent.tool_result" }, transcript.Select(e => e.Type).ToArray());
        StringAssert.Contains(transcript[1].PayloadJson, "README.md");
        Assert.AreEqual(3L, await ScalarAsync<long>(db, "SELECT count(*) FROM agentd.agent_prompt_events"));

        await log.DeleteBeforeAsync(DateTimeOffset.UtcNow.AddMinutes(1), default);
        Assert.AreEqual(0L, await ScalarAsync<long>(db, "SELECT count(*) FROM agentd.agent_prompt_events"), "deleted with its prompt");
    }

    [TestMethod]
    public async Task A_job_turns_transcript_is_its_jobs_agent_events_while_it_ran()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_job_transcript");
        var log = new PromptLogStore(db);
        await ExecuteAsync(db, "INSERT INTO agentd.jobs (work_item_id, state, repo, title) VALUES (5701, 'Running', 'sysmin', 't')");
        var job = await ScalarAsync<long>(db, "SELECT id FROM agentd.jobs");
        await ExecuteAsync(db, $"INSERT INTO agentd.events (job_id, type, payload, ts) VALUES ({job}, 'agent.text', '{{\"text\":\"before\"}}', now() - interval '1 hour')");
        var id = await log.StartAsync(Prompt("rules", sourceId: job), default);
        await ExecuteAsync(db, $"INSERT INTO agentd.events (job_id, type, payload) VALUES ({job}, 'agent.text', '{{\"text\":\"during\"}}'), ({job}, 'job.state', '{{}}'), ({job}, 'agent.tool_call', '{{\"name\":\"Bash\"}}')");
        await log.FinishAsync(id, PromptOutcomes.Ok, null, null, default);
        await ExecuteAsync(db, $"INSERT INTO agentd.events (job_id, type, payload, ts) VALUES ({job}, 'agent.text', '{{\"text\":\"after\"}}', now() + interval '1 hour')");

        var transcript = await log.TranscriptAsync(id, 100, default);

        CollectionAssert.AreEqual(new[] { "agent.text", "agent.tool_call" }, transcript.Select(e => e.Type).ToArray(), "only the agent's events, only while the turn ran");
        StringAssert.Contains(transcript[0].PayloadJson, "during");
    }

    [TestMethod]
    public async Task Parallel_turns_write_their_transcripts_without_mixing()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_transcript_parallel");
        var log = new PromptLogStore(db);
        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => log.StartAsync(Prompt("rules", $"turn {i}", i) with { Source = PromptSources.Chat, Step = null }, default)));

        await Task.WhenAll(ids.Select(id => Task.Run(() => log.AddEventsAsync(id,
            [.. Enumerable.Range(0, 50).Select(n => new PromptEvent("agent.text", $"{{\"text\":\"{id}-{n}\"}}", DateTimeOffset.UtcNow))], default))));

        foreach (var id in ids)
        {
            var transcript = await log.TranscriptAsync(id, 100, default);
            CollectionAssert.AreEqual(Enumerable.Range(0, 50).Select(n => $"{id}-{n}").ToArray(),
                transcript.Select(e => System.Text.Json.JsonDocument.Parse(e.PayloadJson).RootElement.GetProperty("text").GetString()).ToArray());
        }
    }

    [TestMethod]
    public async Task Prompts_group_into_conversations_latest_activity_first_and_one_reads_whole_in_order()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_conversations");
        var log = new PromptLogStore(db);
        var idea1 = await log.StartAsync(Prompt("idea rules", "Dark mode please", 7) with { Source = PromptSources.Idea, Step = null, Model = "sonnet" }, default);
        var job1 = await log.StartAsync(Prompt("job rules", "Fix the login", 42), default);
        var idea2 = await log.StartAsync(Prompt("idea rules", "Only the portal", 7) with { Source = PromptSources.Idea, Step = null, Model = "opus" }, default);
        await log.FinishAsync(idea1, PromptOutcomes.Ok, null, "Which pages?", default);
        await log.FinishAsync(idea2, PromptOutcomes.Error, "boom", null, default);

        var all = await log.ConversationsAsync(null, null, 50, default);
        var ideas = await log.ConversationsAsync(PromptSources.Idea, null, 50, default);
        var older = await log.ConversationsAsync(null, idea2, 50, default);

        Assert.AreEqual(("idea", 7L, 2, idea2, "error", false), (all[0].Source, all[0].SourceId, all[0].Turns, all[0].LastId, all[0].LastOutcome, all[0].Running));
        CollectionAssert.AreEqual(new[] { "opus", "sonnet" }, all[0].Models.ToArray());
        Assert.AreEqual("Dark mode please", all[0].Preview, "the first prompt");
        Assert.AreEqual(("job", 42L, true), (all[1].Source, all[1].SourceId, all[1].Running), "still running");
        Assert.HasCount(1, ideas);
        CollectionAssert.AreEqual(new[] { job1 }, older.Select(c => c.LastId).ToArray(), "the page below a conversation's latest prompt");

        var turns = await log.ConversationAsync(PromptSources.Idea, 7, 100, default);
        CollectionAssert.AreEqual(new[] { idea1, idea2 }, turns.Select(t => t.Id).ToArray(), "oldest first");
        Assert.AreEqual(("Dark mode please", "Which pages?", "idea rules"), (turns[0].Prompt, turns[0].Reply, turns[0].Rules));
        Assert.IsEmpty(await log.ConversationAsync(PromptSources.Chat, 7, 100, default));
    }

    [TestMethod]
    public async Task Parallel_readers_see_the_same_conversations_while_turns_are_added()
    {
        await using var db = await Database.CreateMigratedAsync("prompt_log_conversations_parallel");
        var log = new PromptLogStore(db);
        for (var i = 0; i < 6; i++)
        {
            await log.StartAsync(Prompt("rules", $"turn {i}", i % 3) with { Source = PromptSources.Chat, Step = null }, default);
        }

        var writes = Enumerable.Range(0, 6).Select(i => log.StartAsync(Prompt("rules", $"more {i}", 100 + i), default));
        var reads = Enumerable.Range(0, 12).Select(_ => log.ConversationsAsync(PromptSources.Chat, null, 50, default));
        await Task.WhenAll(writes);
        var lists = await Task.WhenAll(reads);

        Assert.IsTrue(lists.All(l => l.Count == 3 && l.Sum(c => c.Turns) == 6), "each reader its own connection; chats only");
    }

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
