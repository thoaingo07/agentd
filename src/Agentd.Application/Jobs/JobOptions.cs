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

    /// <summary>Back-off when a usage limit reports no reset time.</summary>
    public TimeSpan UsageLimitBackoff { get; set; } = TimeSpan.FromMinutes(30);
}
