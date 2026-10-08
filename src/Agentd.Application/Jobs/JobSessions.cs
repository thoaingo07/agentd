using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Jobs;

/// <summary>
/// A job's agent session per model profile. Each provider keeps its own conversation: a step that moves to another
/// profile starts (or resumes) that profile's session instead of replaying the conversation on a different model.
/// </summary>
public interface IJobSessions
{
    /// <summary>The profile's session for the job; <paramref name="proposed"/> becomes it (Created = true) when it has none yet.</summary>
    Task<(Guid Session, bool Created)> GetOrCreateAsync(JobId job, string profile, Guid proposed, CancellationToken cancellationToken);
}

/// <summary>The plan the agent submitted, kept for a session that starts mid-job (its handoff).</summary>
public interface IJobPlans
{
    Task SaveAsync(JobId job, string plan, DateTimeOffset submittedAt, CancellationToken cancellationToken);

    Task<string?> GetAsync(JobId job, CancellationToken cancellationToken);
}
