namespace Agentd.Application.Jobs;

/// <summary>Work item selection and job behavior (configuration section <c>Agentd:Jobs</c>).</summary>
public sealed class JobOptions
{
    public const string Section = "Agentd:Jobs";

    /// <summary>Work items carrying this tag are picked up.</summary>
    public string Tag { get; set; } = "ai-workflow";

    /// <summary>Added when agentd claims a work item; excluded from polling.</summary>
    public string ClaimTag { get; set; } = "ai-in-progress";

    public IList<string> States { get; } = ["New", "Active", "To Do", "Doing"];

    public string BranchPrefix { get; set; } = "ai/";

    /// <summary>Failed push/PR attempts before a publishing job fails.</summary>
    public int PublishMaxAttempts { get; set; } = 3;

    /// <summary>First retry delay for publishing; doubles on each further attempt.</summary>
    public TimeSpan PublishRetryDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How long a job waits for a developer's answer before it fails (reminders at 50% and 90%).</summary>
    public TimeSpan WaitForHumanTimeout { get; set; } = TimeSpan.FromDays(3);

    /// <summary>How often the thread's heartbeat is re-posted (the previous one is deleted).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A running agent with no tool activity for this long gets a "no activity" warning.</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Back-off when a usage limit reports no reset time.</summary>
    public TimeSpan UsageLimitBackoff { get; set; } = TimeSpan.FromMinutes(30);
}
