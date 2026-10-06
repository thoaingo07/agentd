using System.Globalization;
using System.Text.RegularExpressions;
using Agentd.Application.Abstractions;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// Removes checkouts nothing uses anymore (every <see cref="JobOptions.WorktreeSweepInterval"/>), then prunes git's list:
/// <c>wi-&lt;id&gt;</c> once its job is over (failed: after <see cref="JobOptions.RetainFailedWorktrees"/>, done: after
/// <see cref="JobOptions.RetainFinishedWorktrees"/>), <c>idea-&lt;id&gt;</c> once the idea is finished, <c>review-&lt;id&gt;</c>
/// once the review is closed or discarded. Unknown folders are left alone. Branches are never deleted.
/// Returns the number of checkouts removed.
/// </summary>
public sealed record SweepWorktrees;

public sealed partial class SweepWorktreesHandler(
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IJobRepository jobs,
    IWorkItemHistory history,
    IClock clock,
    IOptions<JobOptions> options,
    IIdeaStore? ideas = null,
    IReviewStore? reviews = null) : ICommandHandler<SweepWorktrees, int>
{
    public async Task<Result<int>> Handle(SweepWorktrees command, CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var repo in await repositories.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var folder in await worktrees.ListFoldersAsync(repo, cancellationToken).ConfigureAwait(false))
            {
                if (await UnusedAsync(folder.Name, cancellationToken).ConfigureAwait(false))
                {
                    await worktrees.RemoveAsync(repo, new WorktreePath(folder.Path), cancellationToken).ConfigureAwait(false);
                    removed++;
                }
            }

            await worktrees.PruneAsync(repo, cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    private async Task<bool> UnusedAsync(string name, CancellationToken ct)
    {
        if (Folder().Match(name) is not { Success: true } match)
        {
            return false;   // not one of agentd's: leave it
        }

        var id = long.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
        switch (match.Groups["kind"].Value)
        {
            case "wi":
                if (await jobs.FindActiveByWorkItemAsync(WorkItemId.From((int)id), ct).ConfigureAwait(false) is not null)
                {
                    return false;
                }

                var latest = (await history.ListJobsAsync(WorkItemId.From((int)id), ct).ConfigureAwait(false)).MaxBy(j => j.CreatedAt);
                var age = latest is null ? TimeSpan.MaxValue : clock.UtcNow - latest.UpdatedAt;
                return latest?.State switch
                {
                    JobState.Failed => age >= options.Value.RetainFailedWorktrees,
                    JobState.Done => age >= options.Value.RetainFinishedWorktrees,
                    _ => true,
                };
            case "idea":
                return ideas is not null && await ideas.GetAsync(id, ct).ConfigureAwait(false) is not { Status: IdeaStatus.Brainstorming or IdeaStatus.Proposed };
            default:
                return reviews is not null && await reviews.GetAsync(id, ct).ConfigureAwait(false) is null or { Status: ReviewStatus.Closed or ReviewStatus.Discarded };
        }
    }

    [GeneratedRegex(@"^(?<kind>wi|idea|review)-(?<id>\d{1,9})$")]
    private static partial Regex Folder();
}
