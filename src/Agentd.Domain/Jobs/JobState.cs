namespace Agentd.Domain.Jobs;

/// <summary>Lifecycle of a job. Phase 2 adds WaitingForHuman; Phase 4 adds phases within Running.</summary>
public enum JobState
{
    Queued,
    Preparing,
    Running,
    Publishing,
    Done,
    Failed,
    Cancelled,
}

public static class JobStateExtensions
{
    public static bool IsTerminal(this JobState state) =>
        state is JobState.Done or JobState.Failed or JobState.Cancelled;
}
