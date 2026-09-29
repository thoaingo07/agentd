using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Ports;

/// <summary>Jobs, persisted through PostgreSQL routines (Infrastructure.Persistence).</summary>
public interface IJobRepository
{
    Task<Job?> GetAsync(JobId id, CancellationToken cancellationToken);

    Task<Job?> FindActiveByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken);

    /// <summary>Inserts a new job and its pending events. Conflict if an active job already exists for the work item.</summary>
    Task<Result> AddAsync(Job job, CancellationToken cancellationToken);

    /// <summary>Saves state + pending events atomically. Conflict if the stored version changed.</summary>
    Task<Result> SaveAsync(Job job, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically claims the oldest runnable queued job (<c>NotBefore</c> passed) and moves it to
    /// <see cref="JobState.Preparing"/> (<c>FOR UPDATE SKIP LOCKED</c>). Null when nothing is runnable.
    /// </summary>
    Task<Job?> DequeueNextAsync(string worker, CancellationToken cancellationToken);

    Task<IReadOnlyList<Job>> ListByStateAsync(IReadOnlyCollection<JobState> states, CancellationToken cancellationToken);

    Task<IReadOnlyList<Job>> ListRecentAsync(TimeSpan window, CancellationToken cancellationToken);
}

/// <summary>Append-only event log (agent output and other non-domain events).</summary>
public interface IEventStore
{
    Task<long> AppendAsync(JobId? jobId, string type, string payloadJson, CancellationToken cancellationToken);
}

/// <summary>Registered repositories (database-backed, config-seeded).</summary>
public interface IRepositoryRegistry
{
    Task<IReadOnlyList<Repository>> ListAsync(CancellationToken cancellationToken);

    Task<Repository?> GetAsync(RepositoryName name, CancellationToken cancellationToken);
}
