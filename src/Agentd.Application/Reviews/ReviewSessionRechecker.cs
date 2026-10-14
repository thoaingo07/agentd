using System.Collections.Concurrent;
using Agentd.Application.Artifacts;
using Agentd.Application.AzureDevOps;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Application.Reviews;

/// <summary>
/// Re-checks a review page's Send after pushes (review-sessions.md §4), like a chat <c>!review</c>: on each review monitor pass,
/// a Send whose PR has a new head, or new replies on a finding's thread, while findings are open, gets one agent turn on a
/// checkout of the new head (<see cref="PostedRechecks"/>); then the PR's threads and main message are updated under the
/// reviewer's own Azure DevOps identity when they connected it. A completed or abandoned PR ends it.
/// </summary>
public sealed partial class ReviewSessionRechecker(
    IReviewSessionStore sessions,
    IReviewSessionPosts posts,
    IRepositoryRegistry repositories,
    IPullRequestService pullRequests,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    ILogger<ReviewSessionRechecker>? logger = null,
    IMcpTokenIssuer? tokens = null,
    AdoOnBehalf? onBehalf = null,
    AdoActor? actor = null)
{
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private readonly ILogger _logger = logger ?? NullLogger<ReviewSessionRechecker>.Instance;

    /// <summary>One pass over the open Sends: done when their PR closed, a re-check when something changed (one at a time per Send).</summary>
    public async Task MonitorAsync(CancellationToken ct)
    {
        foreach (var post in await posts.ListOpenAsync(ct).ConfigureAwait(false))
        {
            if (_running.ContainsKey(post.SessionId))
            {
                continue;
            }

            if (await sessions.GetAsync(post.SessionId, ct).ConfigureAwait(false) is not { PullRequestReviewId: null } session
                || RepositoryName.Create(session.Repository) is not { IsSuccess: true } name
                || await repositories.GetAsync(name.Value, ct).ConfigureAwait(false) is not { } repo)
            {
                continue;   // a chat review's session is re-checked in its thread
            }

            var pr = await pullRequests.GetAsync(repo, post.PullRequestId, ct).ConfigureAwait(false);
            if (pr is null || pr.Status != PullRequestStatus.Active)
            {
                await posts.DoneAsync(post.SessionId, ct).ConfigureAwait(false);
                continue;
            }

            if (ReviewFindings.AllResolved(post.Posted))
            {
                continue;
            }

            var threads = post.Posted.Findings.Select(f => f.Thread).OfType<int>().ToHashSet();
            var comments = await pullRequests.ListCommentsAsync(repo, post.PullRequestId, ct).ConfigureAwait(false);
            var fresh = comments.Where(c => threads.Contains(c.ThreadId) && (post.Posted.RepliesSeenUntil is not { } seen || c.PublishedAt > seen)).ToList();
            if (pr.SourceCommit != post.Head || fresh.Count > 0)
            {
                _ = Start(session, repo, post, pr.SourceCommit, comments);
            }
        }
    }

    private Task Start(ReviewSession session, Repository repo, ReviewSessionPost post, string head, IReadOnlyList<PullRequestComment> comments) =>
        _running.GetOrAdd(session.Id, key => Task.Run(async () =>
        {
            try
            {
                await RecheckAsync(session, repo, post, head, comments, CancellationToken.None).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // a re-check that fails is tried again after the next push; the monitor keeps going
            catch (Exception ex)
#pragma warning restore CA1031
            {
                LogFailed(_logger, ex, key);
            }
            finally
            {
                _running.TryRemove(key, out _);
            }
        }));

    /// <summary>The turn and the PR's update. Waited on by tests; the monitor starts it in the background.</summary>
    internal async Task RecheckAsync(ReviewSession session, Repository repo, ReviewSessionPost post, string head, IReadOnlyList<PullRequestComment> comments, CancellationToken ct)
    {
        var fresh = comments.Where(c => post.Posted.RepliesSeenUntil is not { } seen || c.PublishedAt > seen).ToList();
        var posted = comments.Count == 0 ? post.Posted : post.Posted with { RepliesSeenUntil = comments.Max(c => c.PublishedAt) };
        var worktree = await worktrees.CheckoutCommitAsync(repo, $"review-session-{session.Id}", head, ct).ConfigureAwait(false);
        var prompt = PostedRechecks.Prompt(post.Head, post.Posted, fresh, withDetail: true)
            + $"\n\nYou reviewed this PR earlier (your summary: {session.Summary}); the checkout is now at its head {head}.";
        var reply = await ArtifactTurn.RunAsync(agent, tokens, ArtifactSources.ReviewSession, session.Id,
            new BrainstormTurn(session.Id, worktree, Guid.NewGuid(), false, prompt, session.Model, session.Effort, ThreadTurnKind.ReviewSession), ct).ConfigureAwait(false);
        var (_, recheck, problem) = ReviewFindings.ExtractRecheck(reply.Text ?? string.Empty, post.Posted.Findings.Count);
        if (recheck is null)
        {
            // Not again until the next push or reply: the head and replies seen move on, the history says why.
            var why = reply.UsageLimitedUntil is { } until ? $"the Claude usage limit is reached until {until:HH:mm} UTC" : reply.Error ?? problem ?? "the reply had no review-recheck block";
            await posts.UpdateAsync(session.Id, posted, head, Entry(head, posted, 0, why), ct).ConfigureAwait(false);
            return;
        }

        var person = onBehalf is null || actor is null ? null : await onBehalf.ForWebLoginAsync(session.CreatedBy, ct).ConfigureAwait(false);
        (ReviewResult Result, int Added) applied;
        using (actor?.Begin(person))
        {
            applied = await PostedRechecks.ApplyAsync(pullRequests, repo, post.PullRequestId, head, posted, recheck, rechecks: true,
                ex => LogPostFailed(_logger, ex, session.Id), ct).ConfigureAwait(false);
        }

        await posts.UpdateAsync(session.Id, applied.Result, head, Entry(head, applied.Result, applied.Added, null), ct).ConfigureAwait(false);
    }

    private static SessionRecheck Entry(string head, ReviewResult result, int added, string? problem) => new(
        DateTimeOffset.UtcNow, head,
        result.Findings.Count(f => f.Status == ReviewFindings.Fixed),
        result.Findings.Count(f => (f.Status ?? ReviewFindings.Open) == ReviewFindings.Open),
        result.Findings.Count(f => f.Status == ReviewFindings.Closed),
        added, problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review {SessionId}: re-checking the Send failed; tried again after the next push")]
    private static partial void LogFailed(ILogger logger, Exception exception, long sessionId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review {SessionId}: updating the PR after a re-check failed")]
    private static partial void LogPostFailed(ILogger logger, Exception exception, long sessionId);
}
