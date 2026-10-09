using System.Collections.Concurrent;
using System.Globalization;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Application.Reviews;

/// <summary>
/// The reviewer for review sessions (docs/architect/review-sessions.md §3): a read-only checkout of the pinned head, one
/// turn with the review rules, and the findings into the session (Reviewing → Ready, or Failed with why). A few at a time;
/// sessions left Reviewing by a restart are picked up again by <see cref="ResumeAsync"/>.
/// </summary>
public sealed partial class ReviewSessionReviewer(
    IReviewSessionStore store,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    ILogger<ReviewSessionReviewer>? logger = null) : IDisposable
{
    /// <summary>Reviews running at once (each is a Claude Code process).</summary>
    public const int MaxConcurrent = 2;

    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private readonly ILogger _logger = logger ?? NullLogger<ReviewSessionReviewer>.Instance;

    /// <summary>Starts the review of session <paramref name="id"/> in the background (once; a second call while it runs does nothing).</summary>
    public Task Start(long id) => _running.GetOrAdd(id, key => Task.Run(async () =>
    {
        try
        {
            await _slots.WaitAsync().ConfigureAwait(false);
            try
            {
                await RunAsync(key, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _slots.Release();
            }
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }));

    /// <summary>Restarts the reviews a restart interrupted (still Reviewing, not running here).</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        foreach (var session in await store.ListByStatusAsync(ReviewSessionStatus.Reviewing, cancellationToken).ConfigureAwait(false))
        {
            _ = Start(session.Id);
        }
    }

    /// <summary>One review, start to finish. Never throws: a failure becomes the session's Failed status and reason.</summary>
    public async Task RunAsync(long id, CancellationToken cancellationToken)
    {
        try
        {
            if (await store.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { Status: ReviewSessionStatus.Reviewing, BaseCommit: { } baseCommit, HeadCommit: { } head } session)
            {
                return;
            }

            if (RepositoryName.Create(session.Repository) is not { IsSuccess: true } name || await repositories.GetAsync(name.Value, cancellationToken).ConfigureAwait(false) is not { } repo)
            {
                await FailAsync(id, $"The repository '{session.Repository}' is no longer registered.", cancellationToken).ConfigureAwait(false);
                return;
            }

            var worktree = await worktrees.CheckoutCommitAsync(repo, $"review-session-{id}", head, cancellationToken).ConfigureAwait(false);
            await store.PinAsync(id, baseCommit, head, worktree, cancellationToken).ConfigureAwait(false);
            var reply = await agent.RunAsync(new BrainstormTurn(id, worktree, Guid.NewGuid(), false, Prompt(session, repo.Name.Value), session.Model, session.Effort, ThreadTurnKind.ReviewSession),
                cancellationToken).ConfigureAwait(false);
            if (reply.UsageLimitedUntil is { } until)
            {
                await FailAsync(id, string.Create(CultureInfo.InvariantCulture, $"The Claude usage limit is reached until {until:HH:mm} UTC. Start the review again after that."), cancellationToken).ConfigureAwait(false);
                return;
            }

            var (_, result, problem) = ReviewFindings.Extract(reply.Text ?? string.Empty);
            if (result is null)
            {
                await FailAsync(id, reply.Text is null ? $"The reviewer didn't answer ({reply.Error ?? "no reply"})." : $"The reviewer's findings couldn't be read: {problem ?? "no review-findings block"}.", cancellationToken).ConfigureAwait(false);
                return;
            }

            await store.AddFindingsAsync(id, [.. result.Findings.Select(SessionFinding.From)], result.Summary, cancellationToken).ConfigureAwait(false);
            await store.SetStatusAsync(id, ReviewSessionStatus.Ready, null, null, cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // any failure is the session's, shown on its page
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(_logger, ex, id);
            await FailAsync(id, $"The review failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>What to review: the pinned change, compared like a PR with its target (the review rules' frame).</summary>
    internal static string Prompt(ReviewSession s, string repository)
    {
        var what = s.Target switch
        {
            ReviewTargets.PullRequest => $"pull request !{s.PullRequestId} (`{s.HeadRef}` → `{s.BaseRef}`)",
            ReviewTargets.Branch => $"the branch `{s.HeadRef}` against `{s.BaseRef}` (not a pull request)",
            _ => $"the commits from `{s.BaseRef}` to `{s.HeadRef}` (not a pull request)",
        };
        return string.Create(CultureInfo.InvariantCulture,
            $"Review {what} in {repository}, requested by {s.CreatedBy} on agentd's review page. Your checkout is the head, {s.HeadCommit}. ") +
            $"The change is exactly `git diff {s.BaseCommit} {s.HeadCommit}` (and `git log {s.BaseCommit}..{s.HeadCommit}`): review that, not later commits on the base. " +
            "There are no PR threads or linked work items to read here unless the commit messages name one. " +
            "End with the review-findings block as usual; the developer keeps, edits or drops each finding on the page.";
    }

    public void Dispose() => _slots.Dispose();

    private Task FailAsync(long id, string reason, CancellationToken ct) => store.SetStatusAsync(id, ReviewSessionStatus.Failed, reason, null, ct);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review session {Id} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, long id);
}
