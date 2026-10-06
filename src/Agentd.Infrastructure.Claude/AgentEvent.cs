namespace Agentd.Infrastructure.Claude;

/// <summary>One thing that happened in a Claude Code run, parsed from a stream-json line.</summary>
public abstract record AgentEvent
{
    /// <summary>Event-log type name (e.g. "agent.tool_call").</summary>
    public abstract string LogType { get; }

    public sealed record SessionStarted(string SessionId, string? Model, string? WorkingDirectory) : AgentEvent
    {
        public override string LogType => "agent.session";
    }

    public sealed record AssistantText(string Text) : AgentEvent
    {
        public override string LogType => "agent.text";
    }

    public sealed record ToolCall(string Id, string Name, string InputJson) : AgentEvent
    {
        public override string LogType => "agent.tool_call";
    }

    public sealed record ToolResult(string ToolUseId, string Content, bool IsError, bool Truncated) : AgentEvent
    {
        public override string LogType => "agent.tool_result";
    }

    /// <summary>Subscription usage status. <see cref="Status"/> is "allowed" while usage is fine.</summary>
    public sealed record RateLimit(string Status, DateTimeOffset? ResetsAt, string? LimitType, IReadOnlyDictionary<string, double> Utilization) : AgentEvent
    {
        public override string LogType => "agent.rate_limit";

        /// <summary>True when the provider refused work because a usage limit was reached.</summary>
        public bool IsLimited => !Status.StartsWith("allowed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The final event of a run.</summary>
    public sealed record TurnResult(string Subtype, bool IsError, int Turns, decimal? CostUsd, long? DurationMs, string? Result, int? ApiErrorStatus) : AgentEvent
    {
        public override string LogType => "agent.result";
    }

    /// <summary>
    /// A command the CLI tracks as a task (<c>system/task_started</c>): its output is written live to a file named after the
    /// task (see <c>CommandOutput</c>). Seen with Claude Code 2.1.290; agentd uses it best effort.
    /// </summary>
    public sealed record TaskStarted(string TaskId, string? ToolUseId, string? SessionId) : AgentEvent
    {
        public override string LogType => "agent.task_started";
    }

    /// <summary>A recognized line agentd doesn't interpret (e.g. system notifications).</summary>
    public sealed record Other(string Type, string? Subtype) : AgentEvent
    {
        public override string LogType => "agent.other";
    }

    /// <summary>A line that isn't valid stream-json (kept for the transcript, never fatal).</summary>
    public sealed record Unparseable(string Line) : AgentEvent
    {
        public override string LogType => "agent.unparseable";
    }
}
