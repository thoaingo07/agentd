using Agentd.Infrastructure.Claude;

namespace Agentd.Infrastructure.Tests.Claude;

[TestClass]
public sealed class StreamJsonParserTests
{
    private static List<AgentEvent> ParseFixture(string name) =>
        File.ReadLines(Path.Combine(AppContext.BaseDirectory, "Claude", "Fixtures", name)).SelectMany(StreamJsonParser.Parse).ToList();

    [TestMethod]
    public void Real_text_transcript_yields_session_text_rate_limit_and_result()
    {
        var events = ParseFixture("simple-text.jsonl");

        var session = events.OfType<AgentEvent.SessionStarted>().Single();
        Assert.AreEqual("11111111-2222-3333-4444-555555555555", session.SessionId);
        Assert.AreEqual("pong", events.OfType<AgentEvent.AssistantText>().Single().Text);

        var limit = events.OfType<AgentEvent.RateLimit>().Single();
        Assert.IsFalse(limit.IsLimited);
        Assert.IsNotNull(limit.ResetsAt);
        Assert.IsTrue(limit.Utilization.ContainsKey("five_hour"));

        var result = events.OfType<AgentEvent.TurnResult>().Single();
        Assert.AreEqual("success", result.Subtype);
        Assert.IsFalse(result.IsError);
        Assert.AreEqual(1, result.Turns);
        Assert.AreEqual("pong", result.Result);
    }

    [TestMethod]
    public void Real_tool_transcript_yields_the_tool_call_and_its_result()
    {
        var events = ParseFixture("tool-use.jsonl");

        var call = events.OfType<AgentEvent.ToolCall>().Single();
        Assert.AreEqual("Read", call.Name);
        StringAssert.Contains(call.InputJson, "/work/probe/note.txt");

        var result = events.OfType<AgentEvent.ToolResult>().Single();
        Assert.AreEqual(call.Id, result.ToolUseId);
        StringAssert.Contains(result.Content, "banana");
        Assert.IsFalse(result.IsError);

        Assert.AreEqual(2, events.OfType<AgentEvent.TurnResult>().Single().Turns);
        Assert.IsTrue(events.OfType<AgentEvent.Other>().Any(o => o.Type == "system"), "non-init system lines are kept as Other");
    }

    [TestMethod]
    public void A_rejected_rate_limit_is_a_usage_limit_with_its_reset_time()
    {
        var events = StreamJsonParser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1790752800,"rateLimitType":"five_hour","unifiedWindows":{"five_hour":{"utilization":1.0,"resetsAt":1790752800}}}}""");

        var limit = (AgentEvent.RateLimit)events.Single();
        Assert.IsTrue(limit.IsLimited);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1790752800), limit.ResetsAt);
        Assert.AreEqual(1.0, limit.Utilization["five_hour"]);
    }

    [TestMethod]
    public void Error_results_keep_subtype_and_api_status()
    {
        var result = (AgentEvent.TurnResult)StreamJsonParser.Parse("""{"type":"result","subtype":"error_max_turns","is_error":true,"num_turns":200,"api_error_status":429}""").Single();

        Assert.IsTrue(result.IsError);
        Assert.AreEqual("error_max_turns", result.Subtype);
        Assert.AreEqual(429, result.ApiErrorStatus);
    }

    [TestMethod]
    public void Long_tool_results_are_capped()
    {
        var big = new string('x', StreamJsonParser.MaxToolResultLength + 50);
        var line = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"BIG"}]}}""".Replace("BIG", big, StringComparison.Ordinal);

        var result = (AgentEvent.ToolResult)StreamJsonParser.Parse(line).Single();

        Assert.IsTrue(result.Truncated);
        Assert.AreEqual(StreamJsonParser.MaxToolResultLength, result.Content.Length);
    }

    [TestMethod]
    [DataRow("not json")]
    [DataRow("{\"type\":")]
    public void Malformed_lines_never_throw(string line) =>
        Assert.IsInstanceOfType<AgentEvent.Unparseable>(StreamJsonParser.Parse(line).Single());

    [TestMethod]
    public void Blank_lines_yield_nothing() => Assert.IsEmpty(StreamJsonParser.Parse("   "));

    [TestMethod]
    public void Tracker_detects_usage_limits_and_builds_the_summary()
    {
        var tracker = new RunTracker();
        foreach (var e in StreamJsonParser.Parse("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1790752800}}"""))
        {
            tracker.Observe(e);
        }

        Assert.IsTrue(tracker.UsageLimited);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeSeconds(1790752800), tracker.LimitResetsAt);

        var ok = new RunTracker();
        foreach (var e in ParseFixture("simple-text.jsonl"))
        {
            ok.Observe(e);
        }

        Assert.IsFalse(ok.UsageLimited);
        Assert.AreEqual(1, ok.Summary!.Turns);
        Assert.AreEqual("11111111-2222-3333-4444-555555555555", ok.SessionId);
    }

    [TestMethod]
    public void Payload_json_is_camel_case_without_the_log_type()
    {
        var json = StreamJsonParser.ToPayloadJson(new AgentEvent.ToolCall("t1", "Bash", "{}"));

        StringAssert.Contains(json, "\"name\":\"Bash\"");
        StringAssert.Contains(json, "\"inputJson\":\"{}\"");
    }

    [TestMethod]
    public void A_task_started_line_names_the_task_whose_output_is_written_live()
    {
        var events = StreamJsonParser.Parse("""{"type":"system","subtype":"task_started","task_id":"bhvzzrmt0","tool_use_id":"toolu_1","description":"Run the build","is_backgrounded":false,"task_type":"local_bash","session_id":"c00b3d6c"}""");

        Assert.AreEqual(new AgentEvent.TaskStarted("bhvzzrmt0", "toolu_1", "c00b3d6c"), events.Single());
        Assert.AreEqual("agent.task_started", events[0].LogType);
    }
}

