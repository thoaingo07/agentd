namespace Agentd.Application.Jobs;

/// <summary>The daemon loop: polling and agent concurrency (configuration section <c>Agentd:Scheduler</c>).</summary>
public sealed class SchedulerOptions
{
    public const string Section = "Agentd:Scheduler";

    /// <summary>False disables polling, scheduling and startup recovery (e.g. a UI-only host or tests).</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Upper bound for the polling back-off after consecutive failures.</summary>
    public TimeSpan MaxPollBackoff { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Agent processes running at the same time.</summary>
    public int MaxConcurrent { get; set; } = 2;

    /// <summary>Wait between dequeue attempts when nothing is queued (a claim wakes the scheduler earlier).</summary>
    public TimeSpan IdleDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>On shutdown, how long running agents may finish before their processes are killed.</summary>
    public TimeSpan ShutdownGrace { get; set; } = TimeSpan.FromSeconds(10);
}
