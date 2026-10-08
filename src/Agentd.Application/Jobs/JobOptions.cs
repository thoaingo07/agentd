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

    /// <summary>The agent works read-only until the developer approves its plan (skip per item with <see cref="AutoTag"/>).</summary>
    public bool RequirePlanApproval { get; set; } = true;

    /// <summary>Work item tag that skips plan approval (the plan is still posted).</summary>
    public string AutoTag { get; set; } = "ai-auto";

    /// <summary>After the PR opens, watch it for review comments and merge (In Review) instead of finishing at once.</summary>
    public bool ReviewLoop { get; set; } = true;

    /// <summary>After the PR is merged, hand off the knowledge and learnings (proposal, agreement, sync PR).</summary>
    public bool Handoff { get; set; } = true;

    /// <summary>How often PRs in review are checked.</summary>
    public TimeSpan ReviewPollInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Fix rounds per job before agentd asks the developer to take over.</summary>
    public int MaxFixRounds { get; set; } = 5;

    /// <summary>How often the thread's heartbeat is re-posted (the previous one is deleted).</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A running agent with no tool activity for this long gets a "no activity" warning.</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Back-off when a usage limit reports no reset time.</summary>
    public TimeSpan UsageLimitBackoff { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long the agent waits for someone to answer a permission request; then it's denied.</summary>
    public TimeSpan PermissionTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// <see cref="PermissionMode.Ask"/> (default): tool calls outside the allowlist are asked in chat.
    /// <see cref="PermissionMode.Auto"/>: they're allowed without asking (still recorded), except the hard denies.
    /// </summary>
    public PermissionMode PermissionMode { get; set; } = PermissionMode.Ask;

    /// <summary>A failed job's worktree is kept this long, so <c>!retry</c> resumes the session; then the sweep removes it (the branch stays).</summary>
    public TimeSpan RetainFailedWorktrees { get; set; } = TimeSpan.FromDays(3);

    /// <summary>A done job's worktree (or its follow-up checkout) is kept this long for talk-only questions after the merge.</summary>
    public TimeSpan RetainFinishedWorktrees { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often unused checkouts are removed (<see cref="SweepWorktrees"/>).</summary>
    public TimeSpan WorktreeSweepInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A model (and effort) per cycle step: <c>plan</c>, <c>implement</c>, <c>fix</c>, <c>handoff</c> (see <see cref="JobSteps"/>).</summary>
    public IDictionary<string, StepModel> Steps { get; } = new Dictionary<string, StepModel>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>How agentd answers an agent's tool calls outside its allowlist.</summary>
public enum PermissionMode
{
    /// <summary>Ask a person in the job's thread (and the Web UI).</summary>
    Ask,

    /// <summary>
    /// Allow everything except the hard denies, and record it. The agent can then run any command as agentd's Unix user,
    /// so use it on a dedicated, unprivileged user (docs/architect/deployment.md §7A).
    /// </summary>
    Auto,
}
