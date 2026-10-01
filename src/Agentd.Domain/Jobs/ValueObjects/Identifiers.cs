using Agentd.Domain.Common;

namespace Agentd.Domain.Jobs.ValueObjects;

/// <summary>Database identity of a job.</summary>
public readonly record struct JobId(long Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Azure DevOps work item id (positive).</summary>
public readonly record struct WorkItemId
{
    private WorkItemId(int value) => Value = value;

    public int Value { get; }

    public static Result<WorkItemId> Create(int value) =>
        value > 0 ? new WorkItemId(value) : DomainError.Validation($"Work item id must be positive (was {value}).");

    /// <summary>For trusted sources (e.g. database rows) where the value was validated on the way in.</summary>
    public static WorkItemId From(int value) =>
        Create(value) is { IsSuccess: true } r ? r.Value : throw new ArgumentOutOfRangeException(nameof(value), value, "Work item id must be positive.");

    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The Claude Code session id owned by agentd for this job (a GUID).</summary>
public readonly record struct ClaudeSessionId(Guid Value)
{
    public static ClaudeSessionId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString("D");
}
