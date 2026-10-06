using System.Globalization;
using System.Text.Json;

namespace Agentd.Infrastructure.Claude;

/// <summary>
/// Parses Claude Code <c>--output-format stream-json</c> lines. One line can hold several content blocks,
/// so each line yields zero or more events. Unknown shapes never throw.
/// </summary>
public static class StreamJsonParser
{
    /// <summary>Tool results are capped in the event log; the full transcript keeps everything.</summary>
    public const int MaxToolResultLength = 8_000;

    public static IReadOnlyList<AgentEvent> Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = Str(root, "type") ?? "unknown";
            var subtype = Str(root, "subtype");
            return type switch
            {
                "system" when subtype == "init" => [new AgentEvent.SessionStarted(Str(root, "session_id") ?? string.Empty, Str(root, "model"), Str(root, "cwd"))],
                "system" when subtype == "task_started" && Str(root, "task_id") is { Length: > 0 } task =>
                    [new AgentEvent.TaskStarted(task, Str(root, "tool_use_id"), Str(root, "session_id"))],
                "assistant" => ContentBlocks(root, assistant: true),
                "user" => ContentBlocks(root, assistant: false),
                "rate_limit_event" => [ParseRateLimit(root)],
                "result" => [ParseResult(root, subtype)],
                _ => [new AgentEvent.Other(type, subtype)],
            };
        }
        catch (JsonException)
        {
            return [new AgentEvent.Unparseable(line)];
        }
    }

    private static List<AgentEvent> ContentBlocks(JsonElement root, bool assistant)
    {
        var events = new List<AgentEvent>();
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
        {
            return events;
        }

        foreach (var block in content.EnumerateArray())
        {
            switch (Str(block, "type"))
            {
                case "text" when assistant && Str(block, "text") is { Length: > 0 } text:
                    events.Add(new AgentEvent.AssistantText(text));
                    break;
                case "tool_use" when assistant:
                    events.Add(new AgentEvent.ToolCall(
                        Str(block, "id") ?? string.Empty,
                        Str(block, "name") ?? "unknown",
                        block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}"));
                    break;
                case "tool_result" when !assistant:
                    var (text2, truncated) = Cap(ToolResultText(block));
                    events.Add(new AgentEvent.ToolResult(
                        Str(block, "tool_use_id") ?? string.Empty,
                        text2,
                        block.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True,
                        truncated));
                    break;
            }
        }

        return events;
    }

    private static string ToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
        {
            return string.Empty;
        }

        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString() ?? string.Empty,
            JsonValueKind.Array => string.Join("\n", content.EnumerateArray().Select(p => Str(p, "text")).Where(t => t is not null)),
            _ => content.GetRawText(),
        };
    }

    private static AgentEvent.RateLimit ParseRateLimit(JsonElement root)
    {
        var info = root.TryGetProperty("rate_limit_info", out var i) ? i : default;
        var utilization = new Dictionary<string, double>(StringComparer.Ordinal);
        if (info.ValueKind == JsonValueKind.Object && info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            foreach (var w in windows.EnumerateObject())
            {
                if (w.Value.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var value))
                {
                    utilization[w.Name] = value;
                }
            }
        }

        return new AgentEvent.RateLimit(
            info.ValueKind == JsonValueKind.Object ? Str(info, "status") ?? "allowed" : "allowed",
            info.ValueKind == JsonValueKind.Object && info.TryGetProperty("resetsAt", out var r) && r.TryGetInt64(out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch) : null,
            info.ValueKind == JsonValueKind.Object ? Str(info, "rateLimitType") : null,
            utilization);
    }

    private static AgentEvent.TurnResult ParseResult(JsonElement root, string? subtype) =>
        new(
            subtype ?? "unknown",
            root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True,
            root.TryGetProperty("num_turns", out var n) && n.TryGetInt32(out var turns) ? turns : 0,
            root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDecimal(out var cost) ? cost : null,
            root.TryGetProperty("duration_ms", out var d) && d.TryGetInt64(out var ms) ? ms : null,
            Str(root, "result"),
            root.TryGetProperty("api_error_status", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt32(out var status) ? status : null);

    private static (string Text, bool Truncated) Cap(string text) =>
        text.Length > MaxToolResultLength ? (text[..MaxToolResultLength], true) : (text, false);

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    /// <summary>Event payload for the log (camelCase JSON of the event minus its type).</summary>
    public static string ToPayloadJson(AgentEvent agentEvent) =>
        JsonSerializer.Serialize(agentEvent, agentEvent.GetType(), s_json);

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    internal static string Invariant(double value) => value.ToString(CultureInfo.InvariantCulture);
}
