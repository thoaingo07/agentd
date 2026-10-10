using System.Globalization;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Application.Monitor;

/// <summary>What a fix round on a watched PR is about: the failed build's details, conflicts, and the new comments.</summary>
public sealed record PrFixRequest(PrWatch Watch, Domain.Repositories.Repository Repository, PullRequestDetails PullRequest, BuildDetail? FailedBuild, bool Conflicts,
    IReadOnlyList<PullRequestComment> Comments);

/// <summary>Prepares a fix round and asks in the 👀 thread before pushing (part 3); true when one was prepared.</summary>
public interface IPrFixRounds
{
    Task<bool> PrepareAsync(PrFixRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The PR Monitor's pass over watched PRs (docs/architect/pr-reviewer-and-monitor.md §2.0): a failed PR build, merge conflicts or
/// new comments from people agentd knows start a fix round after 5 quiet minutes, at most <see cref="MaxRounds"/> per PR, never
/// while one waits for an answer. A completed or abandoned PR stops being watched.
/// </summary>
public sealed partial class PrMonitor(
    IPrWatchStore watches,
    IRepositoryRegistry repositories,
    IPullRequestService pullRequests,
    IAzureDevOpsSearch search,
    IAdoUserConnections connections,
    IMessagingProviderRegistry providers,
    IClock clock,
    IPrFixRounds? rounds = null,
    ILogger<PrMonitor>? logger = null)
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(5);
    public const int MaxRounds = 5;

    private readonly ILogger _logger = logger ?? NullLogger<PrMonitor>.Instance;

    public async Task PassAsync(CancellationToken ct)
    {
        foreach (var watch in await watches.ListActiveAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await CheckAsync(watch, ct).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // one PR's trouble (Azure DevOps down, a deleted repo) never stops the others; retried next pass
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogFailed(_logger, ex, watch.PullRequestId);
            }
        }
    }

    public async Task CheckAsync(PrWatch w, CancellationToken ct)
    {
        if (RepositoryName.Create(w.Repository) is not { IsSuccess: true } name || await repositories.GetAsync(name.Value, ct).ConfigureAwait(false) is not { } repo
            || await pullRequests.GetAsync(repo, w.PullRequestId, ct).ConfigureAwait(false) is not { } pr)
        {
            return;
        }

        if (pr.Status != PullRequestStatus.Active)
        {
            await watches.StopAsync(w.Id, ct).ConfigureAwait(false);
            await PostAsync(w, $"🏁 PR !{w.PullRequestId} is {pr.Status.ToString().ToLowerInvariant()}: I stopped watching it.", ct).ConfigureAwait(false);
            return;
        }

        if (w.Pending is not null)
        {
            return;   // a prepared round waits for push / discard
        }

        var comments = await pullRequests.ListCommentsAsync(repo, w.PullRequestId, ct).ConfigureAwait(false);
        var unseen = comments.Where(c => !w.SeenComments.Contains(c.CommentId)).ToList();
        var known = new List<PullRequestComment>();
        foreach (var c in unseen.Where(c => c.IsOpen))
        {
            if (c.AuthorUniqueName is { Length: > 0 } who && await connections.FindByUniqueNameAsync(who, ct).ConfigureAwait(false) is not null)
            {
                known.Add(c);
            }
        }

        // Comments that start nothing (closed threads, people agentd doesn't know) are seen now, so they're not looked at again.
        var seen = w.SeenComments.Concat(unseen.Where(c => !known.Contains(c)).Select(c => c.CommentId)).Distinct().ToList();
        var build = await search.ListBuildsAsync(null, $"refs/pull/{w.PullRequestId}/merge", null, 1, ct).ConfigureAwait(false) is [var newest, ..] ? newest : null;
        var failed = build is { Result: "failed" } && build.Id != w.LastBuildId ? build : null;
        var conflicts = string.Equals(pr.MergeStatus, "conflicts", StringComparison.OrdinalIgnoreCase);
        var now = clock.UtcNow;
        if (known.Count == 0 && failed is null && !conflicts)
        {
            if (w.SignalAt is not null || seen.Count != w.SeenComments.Count)
            {
                await watches.UpdateAsync(w.Id, w.FixRounds, seen, w.LastBuildId, null, null, ct).ConfigureAwait(false);
            }

            return;
        }

        var what = What(failed, conflicts, known.Count);
        if (w.FixRounds >= MaxRounds)
        {
            if (w.SignalAt is null)
            {
                await watches.UpdateAsync(w.Id, w.FixRounds, seen, w.LastBuildId, now, null, ct).ConfigureAwait(false);
                await PostAsync(w, $"⚠️ PR !{w.PullRequestId}: {what}, but I already prepared {MaxRounds} fix rounds. Please take over (or `!unwatch` and `!watch` again to reset).", ct)
                    .ConfigureAwait(false);
            }

            return;
        }

        if (w.SignalAt is not { } since)
        {
            await watches.UpdateAsync(w.Id, w.FixRounds, seen, w.LastBuildId, now, null, ct).ConfigureAwait(false);
            await PostAsync(w, $"🔔 PR !{w.PullRequestId}: {what}. I'll prepare a fix after {Quiet.TotalMinutes:0} quiet minutes.", ct).ConfigureAwait(false);
            return;
        }

        if (now - since < Quiet || (known.Count > 0 && now - known.Max(c => c.PublishedAt) < Quiet))
        {
            return;   // still debouncing: one review pass is one round
        }

        var handled = seen.Concat(known.Select(c => c.CommentId)).Distinct().ToList();
        var lastBuild = failed?.Id ?? w.LastBuildId;
        await watches.UpdateAsync(w.Id, w.FixRounds, handled, lastBuild, null, null, ct).ConfigureAwait(false);
        if (rounds is null)
        {
            await PostAsync(w, $"🔧 PR !{w.PullRequestId}: {what}. (Preparing fixes isn't enabled here yet.)", ct).ConfigureAwait(false);
            return;
        }

        var detail = failed is null ? null : await search.GetBuildAsync(failed.Id, ct).ConfigureAwait(false);
        var updated = w with { SeenComments = handled, LastBuildId = lastBuild, SignalAt = null };
        if (await rounds.PrepareAsync(new PrFixRequest(updated, repo, pr, detail, conflicts, known), ct).ConfigureAwait(false))
        {
            var latest = await watches.GetAsync(w.Id, ct).ConfigureAwait(false);
            await watches.UpdateAsync(w.Id, w.FixRounds + 1, handled, lastBuild, null, latest?.Pending, ct).ConfigureAwait(false);
        }
    }

    /// <summary>"the PR build failed (run 901), merge conflicts with develop and 2 new comment(s)".</summary>
    private static string What(BuildHit? failed, bool conflicts, int comments) =>
        string.Join(", ", new[]
        {
            failed is null ? null : string.Create(CultureInfo.InvariantCulture, $"the PR build failed (run {failed.Id})"),
            conflicts ? "merge conflicts" : null,
            comments > 0 ? string.Create(CultureInfo.InvariantCulture, $"{comments} new comment(s)") : null,
        }.OfType<string>());

    private async Task PostAsync(PrWatch w, string text, CancellationToken ct) =>
        await providers.Resolve(w.Provider).SendAsync(new ConversationRef(w.Provider, w.ThreadId, w.SpaceId), new OutboundMessage(MessageKind.Info, text), ct).ConfigureAwait(false);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking watched PR {Pr} failed; retrying next pass")]
    private static partial void LogFailed(ILogger logger, Exception exception, int pr);
}
