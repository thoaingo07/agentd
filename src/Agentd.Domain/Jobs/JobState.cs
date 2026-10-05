namespace Agentd.Domain.Jobs;

/// <summary>Lifecycle of a job. Phase 4 adds phases within Running.</summary>
public enum JobState
{
    Queued,
    Preparing,
    Running,

    /// <summary>The agent asked the developer a question and waits for the reply.</summary>
    WaitingForHuman,
    Publishing,

    /// <summary>The pull request is open; agentd watches it for review comments until it is merged.</summary>
    InReview,

    /// <summary>Stopped by a developer for now; the session, worktree, branch and thread are kept for a resume.</summary>
    Paused,
    Done,
    Failed,
    Cancelled,
}

public static class JobStateExtensions
{
    public static bool IsTerminal(this JobState state) =>
        state is JobState.Done or JobState.Failed or JobState.Cancelled;
}
