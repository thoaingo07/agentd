using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

namespace Agentd.Application.Ports;

/// <summary>Conversations (a job's thread per messaging provider), persisted through PostgreSQL routines.</summary>
public interface IConversationStore
{
    /// <summary>Inserts a newly opened conversation and its events. Conflict if the thread is taken or the job already has one open on the provider.</summary>
    Task<Result> AddAsync(Conversation conversation, CancellationToken cancellationToken);

    /// <summary>Saves the status message id and the closed time.</summary>
    Task<Result> SaveAsync(Conversation conversation, CancellationToken cancellationToken);

    Task<IReadOnlyList<Conversation>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken);

    Task<Conversation?> FindExternalAsync(ProviderKey provider, string externalConversationId, CancellationToken cancellationToken);

    /// <summary>Open conversations of any job for <paramref name="workItem"/> (one thread per work item across reruns).</summary>
    Task<IReadOnlyList<Conversation>> ListOpenByWorkItemAsync(WorkItemId workItem, CancellationToken cancellationToken);

    /// <summary>Hands <paramref name="conversation"/> to <paramref name="jobId"/> (a new run of the same work item).</summary>
    Task<Result> MoveAsync(Conversation conversation, JobId jobId, CancellationToken cancellationToken);

    /// <summary>Open conversations on a provider (what a polling provider watches).</summary>
    Task<IReadOnlyList<Conversation>> ListOpenAsync(ProviderKey provider, CancellationToken cancellationToken);
}

/// <summary>Chat threads deleted on the provider by hand, found while reading them.</summary>
public interface IDeletedThreads
{
    /// <summary>
    /// Stops treating <paramref name="threadId"/> as open anywhere: a job's conversation closes, a chat or idea carries
    /// on in the Web UI without a thread, a PR review closes. Returns how many were changed (0: nothing used it).
    /// </summary>
    Task<int> MarkDeletedAsync(ProviderKey provider, string threadId, DateTimeOffset at, CancellationToken cancellationToken);
}
