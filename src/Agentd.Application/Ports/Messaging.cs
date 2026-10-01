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
}
