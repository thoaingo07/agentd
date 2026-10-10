using System.Collections.Concurrent;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.AzureDevOps;

/// <summary>
/// Whose name a job's Azure DevOps actions go under (docs/architect/ado-user-delegation.md §1): its work item's Assigned
/// To, when they have a working sign-in; otherwise agentd's own, said once in the job's thread.
/// </summary>
public sealed class AdoOnBehalf(IAdoUserConnections connections, AdoUserTokens tokens, IOutbox outbox, Microsoft.Extensions.Options.IOptions<Reviews.WebLinkOptions>? web = null)
{
    private readonly ConcurrentDictionary<long, byte> _told = new();
    private readonly ConcurrentDictionary<long, byte> _askedEmail = new();

    /// <summary>The identity of whoever connected from this agentd web login, while their sign-in works; else null (agentd's own).</summary>
    public async Task<Guid?> ForWebLoginAsync(string webLogin, CancellationToken cancellationToken)
    {
        foreach (var c in await connections.ListByWebLoginAsync(webLogin, cancellationToken).ConfigureAwait(false))
        {
            if (!c.Failed && await tokens.GetAccessTokenAsync(c.IdentityId, forceRefresh: false, cancellationToken).ConfigureAwait(false) is not null)
            {
                return c.IdentityId;
            }
        }

        return null;
    }

    /// <summary>
    /// Who a job's commits are by (docs/architect/ado-user-delegation.md §1.1): its work item's Assigned To once they've
    /// connected (a PAT or Microsoft's sign-in) and agentd has an email for them. Otherwise null (agentd's), and the job's
    /// thread asks them, once, to add it in Settings → Your Azure DevOps.
    /// </summary>
    public async Task<CommitAuthor?> CommitAuthorAsync(Guid? assigneeId, string? assignee, JobId job, CancellationToken cancellationToken)
    {
        if (assigneeId is not { } id)
        {
            return null;
        }

        var connection = await connections.FindByIdentityAsync(id, cancellationToken).ConfigureAwait(false);
        if (connection?.Author is { } author)
        {
            return author;
        }

        // Not connected: one ask covers the PR too (no "agentd's name" note later). Connected without an email: its own ask.
        if ((connection is null ? _told : _askedEmail).TryAdd(job.Value, 0))
        {
            var who = assignee ?? connection?.DisplayName ?? "The assignee";
            var settings = web?.Value.Link("/settings") ?? "/settings";
            var ask = connection is null
                ? $"👤 {who}, this job's commits and PR go under agentd's name until you add your Azure DevOps: a personal access token (or Connect with Microsoft) in Settings → Your Azure DevOps, {settings}. Add it before the PR opens and they're yours."
                : $"👤 {who}, agentd needs your commit email to commit as you: add it in Settings → Your Azure DevOps, {settings}. Until then this job's commits are agentd's.";
            await outbox.TryEnqueueAsync(job, new OutboundMessage(MessageKind.Info, ask), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

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
