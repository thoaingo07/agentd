using System.Text.Json;
using Agentd.Application.Inbox;
using Agentd.Application.Messaging;
using Agentd.Application.Prompts;
using Agentd.Infrastructure.Persistence;
using Agentd.Infrastructure.Persistence.Repositories;
using NpgsqlTypes;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
public sealed class PgParameterTests
{
    [TestMethod]
    [DataRow("{\"a\":\"x\\u0000y\"}", "{\"a\":\"x\\uFFFDy\"}", DisplayName = "an escaped NUL")]
    [DataRow("{\"a\":\"\\u0000\\u0000\"}", "{\"a\":\"\\uFFFD\\uFFFD\"}", DisplayName = "several")]
    [DataRow("{\"a\":\"C:\\\\u0000\"}", "{\"a\":\"C:\\\\u0000\"}", DisplayName = "an escaped backslash, then the text u0000")]
    [DataRow("{\"a\":\"\\\\\\u0000\"}", "{\"a\":\"\\\\\\uFFFD\"}", DisplayName = "an escaped backslash, then an escaped NUL")]
    [DataRow("{\"a\":\"plain\"}", "{\"a\":\"plain\"}", DisplayName = "nothing to clean")]
    public void Json_turns_each_NUL_escape_into_the_replacement_character(string json, string expected) =>
        Assert.AreEqual(expected, PgParameter.Json(json));

    [TestMethod]
    public void Only_strings_bound_as_jsonb_or_text_are_cleaned()
    {
        Assert.AreEqual("a\uFFFDb", PgParameter.Of("a\0b", NpgsqlDbType.Text).Value);
        Assert.AreEqual("[\"\\uFFFD\"]", PgParameter.Of(JsonSerializer.Serialize(new[] { "\0" }), NpgsqlDbType.Jsonb).Value);
        Assert.AreEqual(DBNull.Value, PgParameter.Of(null, NpgsqlDbType.Jsonb).Value);
        Assert.AreEqual(7L, PgParameter.Of(7L, NpgsqlDbType.Bigint).Value);
    }
}

/// <summary>A tool reading a UTF-16 or binary file: its output carries U+0000, which PostgreSQL can't store as it is.</summary>
[TestClass]
[TestCategory("Integration")]
public sealed class NulCharacterStorageTests
{
    private static readonly string s_binary = "docs/Pg_Tables.md\n\uFFFD\uFFFDD\0e\0s\0c\0";

    [TestMethod]
    public async Task An_agent_event_with_a_NUL_in_its_tool_output_is_stored()
    {
        await using var db = await Database.CreateMigratedAsync("nul_events");
        var events = new EventStore(db);

        var seq = await events.AppendAsync(null, "agent.tool_result", JsonSerializer.Serialize(new { toolUseId = "t1", content = s_binary }), default);

        var stored = await events.GetAsync(seq, default);
        Assert.AreEqual("docs/Pg_Tables.md\n\uFFFD\uFFFDD\uFFFDe\uFFFDs\uFFFDc\uFFFD", stored!.Payload.GetProperty("content").GetString());
    }

    [TestMethod]
    public async Task A_prompt_its_transcript_and_reply_with_NULs_are_stored()
    {
        await using var db = await Database.CreateMigratedAsync("nul_prompts");
        var log = new PromptLogStore(db);

        var id = await log.StartAsync(new PromptStart(PromptSources.Idea, 1, null, Guid.NewGuid(), false, "opus", "high", null, "rules\0", "read\0 it"), default);
        await log.AddEventsAsync(id, [new PromptEvent("agent.tool_result", JsonSerializer.Serialize(new { content = s_binary }), DateTimeOffset.UtcNow)], default);
        await log.FinishAsync(id, PromptOutcomes.Ok, null, "done\0", default);

        var transcript = await log.TranscriptAsync(id, 10, default);
        StringAssert.Contains(transcript.Single().PayloadJson, "Pg_Tables.md");
        var prompt = (await log.GetAsync(id, default))!;
        Assert.AreEqual(("read\uFFFD it", "done\uFFFD", "rules\uFFFD"), (prompt.Prompt, prompt.Reply, prompt.Rules));
    }

    [TestMethod]
    public async Task An_inbox_message_with_a_NUL_is_stored()
    {
        await using var db = await Database.CreateMigratedAsync("nul_inbox");
        var inbox = new InboxStore(db);

        await inbox.AddAsync(new InboxEntry(InboxSources.Chat, 1, "💬", "tngo", new OutboundMessage(MessageKind.Info, "Found:\0 it")), default);

        Assert.AreEqual("Found:\uFFFD it", (await inbox.ThreadAsync(InboxSources.Chat, 1, null, 10, default)).Single().Markdown);
    }
}
