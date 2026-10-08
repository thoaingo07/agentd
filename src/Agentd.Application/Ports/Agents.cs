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
    bool ReadOnly = false,
    string? Step = null,
    string? Model = null,
    string? Effort = null);

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

    /// <summary>The agent process of each running job (the root of its process tree), for resource sampling.</summary>
    IReadOnlyDictionary<long, int> ProcessIds() => new Dictionary<long, int>();
}

/// <summary>Reads CPU, memory and disk use from the operating system (Linux <c>/proc</c>; elsewhere it returns nothing).</summary>
public interface IResourceSampler
{
    /// <summary>CPU (percent of one core, since the previous call) and resident memory of each process tree, by job id.</summary>
    IReadOnlyDictionary<long, (double CpuPercent, long MemoryBytes)> SampleTrees(IReadOnlyDictionary<long, int> roots);

    /// <summary>The machine: CPU busy percent since the previous call, memory, and the disk holding <paramref name="diskPath"/>.</summary>
    Jobs.MachineResources? SampleMachine(string diskPath);
}

/// <summary>What the runner reports while an agent works (fed into <c>JobActivity</c>).</summary>
public interface IAgentActivitySink
{
    void ToolStep(JobId jobId, string description, DateTimeOffset at);

    /// <summary>The tool call's result arrived: nothing is running anymore.</summary>
    void ToolFinished(JobId jobId);

    /// <summary>A long-running command started; its output can be followed (<see cref="Jobs.CommandOutput"/>).</summary>
    void CommandStarted(JobId jobId, string session, string taskId);

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
