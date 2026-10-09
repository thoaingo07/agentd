using System.Collections.Concurrent;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.AzureDevOps;

/// <summary>
/// Whose name a job's Azure DevOps actions go under (docs/architect/ado-user-delegation.md §1): its work item's Assigned
/// To, when they have a working sign-in; otherwise agentd's own, said once in the job's thread.
/// </summary>
public sealed class AdoOnBehalf(IAdoUserConnections connections, AdoUserTokens tokens, IOutbox outbox)
{
    private readonly ConcurrentDictionary<long, byte> _told = new();

    /// <param name="assigneeId">The work item's Assigned To (null: nobody, so agentd's own without a note).</param>
    /// <param name="assignee">Their name, for the note.</param>
    /// <param name="job">The job whose thread gets the note when the person can't be used; null for none.</param>
    /// <param name="cancellationToken">Cancels it.</param>
    /// <returns>The identity to run the calls as (<see cref="AdoActor.Begin"/>), or null for agentd's own.</returns>
    public async Task<Guid?> ResolveAsync(Guid? assigneeId, string? assignee, JobId? job, CancellationToken cancellationToken)
    {
        if (assigneeId is not { } id)
        {
            return null;
        }

        var connection = await connections.FindByIdentityAsync(id, cancellationToken).ConfigureAwait(false);
        if (connection is { Failed: false } && await tokens.GetAccessTokenAsync(id, forceRefresh: false, cancellationToken).ConfigureAwait(false) is not null)
        {
            return id;
        }

        if (job is { } j && _told.TryAdd(j.Value, 0))
        {
            var who = assignee ?? connection?.DisplayName ?? "The assignee";
            var why = connection is null
                ? $"{who} hasn't connected their Azure DevOps"
                : $"{who}'s Azure DevOps sign-in needs reconnecting";
            await outbox.TryEnqueueAsync(j, new OutboundMessage(MessageKind.Info,
                $"🔗 This goes to Azure DevOps under agentd's name: {why} (Settings → Your Azure DevOps)."), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
