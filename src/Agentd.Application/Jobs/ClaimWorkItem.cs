using System.Collections.Concurrent;
using Agentd.Application.Abstractions;
using Agentd.Application.Ports;
using Agentd.Application.Repositories;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// Claim a work item and queue a job for it. <paramref name="Force"/> is used by <c>agentd run</c>;
/// <paramref name="Repository"/> (<c>--repo</c>) picks a registered repository instead of matching.
/// </summary>
public sealed record ClaimWorkItem(WorkItemId WorkItemId, bool Force = false, RepositoryName? Repository = null);

/// <summary>Remembers work items already told "no repository matches", so polling doesn't repeat the comment.</summary>
public sealed class NoMatchNotices
{
    private readonly ConcurrentDictionary<int, byte> _notified = new();

    public bool TryMark(WorkItemId id) => _notified.TryAdd(id.Value, 0);
}

public sealed class ClaimWorkItemHandler(
    IWorkItemSource workItems,
    IRepositoryRegistry repositories,
    IJobRepository jobs,
    IClock clock,
    NoMatchNotices notices,
    IOptions<JobOptions> options,
    AzureDevOps.AdoOnBehalf? onBehalf = null,
    AzureDevOps.AdoActor? actor = null) : ICommandHandler<ClaimWorkItem, JobId>
{
    public async Task<Result<JobId>> Handle(ClaimWorkItem command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (await jobs.FindActiveByWorkItemAsync(command.WorkItemId, cancellationToken).ConfigureAwait(false) is { } active)
        {
            return DomainError.Conflict($"Work item {command.WorkItemId} already has active job {active.Id}.");
        }

        var item = await workItems.GetAsync(command.WorkItemId.Value, cancellationToken).ConfigureAwait(false);
        if (item is null)
        {
            return DomainError.NotFound($"Work item {command.WorkItemId}");
        }

        RepositoryMatch match;
        if (command.Repository is { } chosen)
        {
            if (await repositories.GetAsync(chosen, cancellationToken).ConfigureAwait(false) is not { } repo)
            {
                return DomainError.NotFound($"Repository '{chosen}'");
            }

            match = new RepositoryMatch.Matched(repo);
        }
        else
        {
            match = RepositoryMatcher.Match(await repositories.ListAsync(cancellationToken).ConfigureAwait(false), item);
        }

        switch (match)
        {
            case RepositoryMatch.Matched { Repository: var repository }:
                // The claim tag and the comment go under the work item's Assigned To when they've connected.
                var assignee = onBehalf is null ? null : await onBehalf.ResolveAsync(item.AssignedToId, item.AssignedTo, null, cancellationToken).ConfigureAwait(false);
                using (actor?.Begin(assignee))
                {
                    if (!await workItems.TryClaimAsync(item.Id, item.Rev, options.Value.ClaimTag, cancellationToken).ConfigureAwait(false))
                    {
                        return DomainError.Conflict($"Work item {item.Id} changed while claiming it; will retry on the next poll.");
                    }

                    var job = Job.Create(command.WorkItemId, repository.Name, item.Title, clock);
                    var added = await jobs.AddAsync(job, cancellationToken).ConfigureAwait(false);
                    if (!added.IsSuccess)
                    {
                        return added.Error;
                    }

                    await workItems.AddCommentAsync(item.Id, $"agentd picked this up (job #{job.Id}, repository `{repository.Name}`).", cancellationToken).ConfigureAwait(false);
                    return job.Id;
                }

            case RepositoryMatch.Ambiguous { Candidates: var candidates }:
                await NotifyOnceAsync(command.WorkItemId, $"agentd: several repositories match this item ({string.Join(", ", candidates)}). Add a `repo:<name>` tag to choose one.", cancellationToken).ConfigureAwait(false);
                return DomainError.Validation($"Work item {item.Id} matches several repositories: {string.Join(", ", candidates)}.");

            default:
                var known = (await repositories.ListAsync(cancellationToken).ConfigureAwait(false)).Select(r => r.Name.Value).ToList();
                var message = NoMatchMessage(item.Tags, known);
                await NotifyOnceAsync(command.WorkItemId, message, cancellationToken).ConfigureAwait(false);
                return new DomainError("not_found", message.Replace("agentd: ", string.Empty, StringComparison.Ordinal));
        }
    }

    /// <summary>Names the unknown repository when the item has a <c>repo:</c> tag, and says how to add it.</summary>
    internal static string NoMatchMessage(IReadOnlyList<string> tags, IReadOnlyList<string> known)
    {
        var registered = known.Count == 0 ? "none yet" : string.Join(", ", known.Select(k => $"`{k}`"));
        var tag = tags.Select(t => t.Trim()).FirstOrDefault(t => t.StartsWith("repo:", StringComparison.OrdinalIgnoreCase) && t.Length > 5);
        return tag is null
            ? $"agentd: no registered repository matches this item (registered: {registered}). Add a `repo:<name>` tag."
            : $"agentd: this item is tagged `{tag}`, but agentd doesn't know that repository (registered: {registered}). An admin can add it with `!repo add <clone url> --name {tag[5..]}` in chat, or `agentd repo add` on the server.";
    }

    private async Task NotifyOnceAsync(WorkItemId id, string text, CancellationToken ct)
    {
        if (notices.TryMark(id))
        {
            await workItems.AddCommentAsync(id.Value, text, ct).ConfigureAwait(false);
        }
    }
}
