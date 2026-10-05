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

/// <summary>History search over all jobs (newest first).</summary>
/// <summary>History filters; null means "any".</summary>
/// <param name="States">Job states.</param>
/// <param name="Repository">One repository.</param>
/// <param name="Title">A case-insensitive substring of the title.</param>
/// <param name="WorkItem">An exact work item id (given with <paramref name="Title"/>, either may match).</param>
/// <param name="From">Finished (last changed) at or after.</param>
/// <param name="To">Finished before.</param>
public sealed record JobSearchFilter(
    IReadOnlyCollection<JobState>? States = null,
    RepositoryName? Repository = null,
    string? Title = null,
    WorkItemId? WorkItem = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public interface IJobSearch
{
    /// <summary>The requested page, newest first, and the number of matches counted up to <paramref name="countCap"/> + 1.</summary>
    Task<(IReadOnlyList<Job> Jobs, long Total)> SearchAsync(JobSearchFilter filter, int offset, int limit, int countCap, CancellationToken cancellationToken);
}

/// <summary>The event log: every observable fact (domain events, agent output, messaging). Payloads are redacted at write time.</summary>
public interface IEventStore
{
    Task<long> AppendAsync(JobId? jobId, string type, string payloadJson, CancellationToken cancellationToken);
}

/// <summary>Reads the event log, paging by <c>seq</c> in both directions; results are always ascending.</summary>
public interface IEventReader
{
    /// <summary>Events after <paramref name="afterSeq"/>; <paramref name="jobId"/> null = all jobs, summary types only (no <c>agent.*</c>).</summary>
    Task<IReadOnlyList<Events.AgentEventDto>> ReadAfterAsync(JobId? jobId, long afterSeq, int limit, CancellationToken cancellationToken);

    /// <summary>A job's events before <paramref name="beforeSeq"/> (the newest <paramref name="limit"/> of them), ascending.</summary>
    Task<IReadOnlyList<Events.AgentEventDto>> ReadBeforeAsync(JobId jobId, long beforeSeq, int limit, CancellationToken cancellationToken);

    Task<Events.AgentEventDto?> GetAsync(long seq, CancellationToken cancellationToken);

    /// <summary>The newest committed seq (0 when there are none): where a client that just loaded a snapshot starts streaming.</summary>
    Task<long> LatestSeqAsync(CancellationToken cancellationToken);
}

/// <summary>Registered repositories (database-backed, config-seeded).</summary>
public interface IRepositoryRegistry
{
    Task<IReadOnlyList<Repository>> ListAsync(CancellationToken cancellationToken);

    Task<Repository?> GetAsync(RepositoryName name, CancellationToken cancellationToken);

    /// <summary>Inserts or updates by name. Conflict if another repository already uses the same URL.</summary>
    Task<Result> UpsertAsync(Repository repository, CancellationToken cancellationToken);

    Task<bool> RemoveAsync(RepositoryName name, CancellationToken cancellationToken);
}
