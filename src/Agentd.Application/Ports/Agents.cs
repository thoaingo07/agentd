using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Ports;

/// <summary>What the runner needs to start or resume one agent turn for a job.</summary>
public sealed record AgentRunRequest(
    JobId JobId,
    WorkItemId WorkItemId,
    WorktreePath Worktree,
    ClaudeSessionId Session,
    string Prompt,
    bool Resume,
    bool ReadOnly = false);

/// <summary>Summary of the agent's final <c>result</c> event, when it produced one.</summary>
public sealed record AgentResultSummary(int Turns, string? ErrorSubtype, bool IsError);

/// <summary>How an agent process ended.</summary>
public abstract record AgentRunOutcome
{
    public sealed record Exited(int ExitCode, AgentResultSummary? Summary) : AgentRunOutcome;

    public sealed record TimedOut : AgentRunOutcome;

    public sealed record Cancelled : AgentRunOutcome;

    /// <summary>The Claude subscription (or provider) hit its usage limit; retry after <see cref="ResetAt"/>.</summary>
    public sealed record UsageLimited(DateTimeOffset? ResetAt) : AgentRunOutcome;
}

/// <summary>Runs Claude Code processes (implemented in Infrastructure.Claude).</summary>
public interface IAgentRunner
{
    Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken cancellationToken);

    bool IsRunning(JobId jobId);

    void Cancel(JobId jobId);
}

/// <summary>What the runner reports while an agent works (fed into <c>JobActivity</c>).</summary>
public interface IAgentActivitySink
{
    void ToolStep(JobId jobId, string description, DateTimeOffset at);

    /// <summary>Utilization (0–1) of the 5-hour and weekly windows, when reported.</summary>
    void Usage(JobId jobId, double? fiveHour, double? weekly, DateTimeOffset? resetsAt);
}

/// <summary>Issues and validates the per-job bearer tokens for the MCP endpoint.</summary>
public interface IMcpTokenIssuer
{
    string Issue(JobId jobId);

    JobId? Validate(string token);

    void Revoke(JobId jobId);
}
