using System.Collections.Concurrent;
using System.Globalization;
using Agentd.Application.Artifacts;
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
    ILogger<ReviewSessionReviewer>? logger = null,
    IServiceProvider? services = null,
    IMcpTokenIssuer? tokens = null,
    IWorkItemSource? workItems = null,
    IPullRequestService? pullRequests = null) : IDisposable
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

    /// <summary>Restarts the reviews a restart interrupted (still Reviewing, not running here; a chat <c>!review</c>'s session is reviewed in its thread).</summary>
    public async Task ResumeAsync(CancellationToken cancellationToken)
    {
        foreach (var session in await store.ListByStatusAsync(ReviewSessionStatus.Reviewing, cancellationToken).ConfigureAwait(false))
        {
            if (session.PullRequestReviewId is not null)
            {
                continue;
            }

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
            var context = await WorkItemContextAsync(session, repo, cancellationToken).ConfigureAwait(false);
            var reply = await ArtifactTurn.RunAsync(agent, tokens, ArtifactSources.ReviewSession, id, new BrainstormTurn(id, worktree, Guid.NewGuid(), false, Prompt(session, repo.Name.Value, context), session.Model, session.Effort, ThreadTurnKind.ReviewSession),
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
            await SaveGuideAsync(id, repo, baseCommit, head, reply.Text!, cancellationToken).ConfigureAwait(false);
            await store.SetStatusAsync(id, ReviewSessionStatus.Ready, null, null, cancellationToken).ConfigureAwait(false);
            await NotifyAsync(id).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // any failure is the session's, shown on its page
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(_logger, ex, id);
            await FailAsync(id, $"The review failed: {ex.Message}", CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The guide to the change, fitted to its real files. A missing or unreadable guide never fails the review: the findings
    /// are what matter, and the page falls back to the file list.
    /// </summary>
    private async Task SaveGuideAsync(long id, Domain.Repositories.Repository repo, string baseCommit, string head, string reply, CancellationToken ct)
    {
        var (guide, problem) = ReviewGuides.Extract(reply);
        if (guide is null)
        {
            LogNoGuide(_logger, id, problem ?? "no review-guide block");
            return;
        }

        var change = await worktrees.DiffCommitsAsync(repo, baseCommit, head, ReviewSessionService.MaxDiffBytes, ct).ConfigureAwait(false);
        await store.SetGuideAsync(id, ReviewGuides.Fit(guide, change.Files), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What the change is supposed to do: a PR's linked work items, or the one its branch names (<c>ai/5617-…</c>), with a
    /// task's parent user story. Nothing for two commits.
    /// </summary>
    private async Task<string> WorkItemContextAsync(ReviewSession session, Domain.Repositories.Repository repo, CancellationToken ct)
    {
        if (session.Target is not (ReviewTargets.PullRequest or ReviewTargets.Branch))
        {
            return string.Empty;
        }

        var linked = session is { PullRequestId: { } pr } && pullRequests is not null
            ? (await pullRequests.GetAsync(repo, pr, ct).ConfigureAwait(false))?.WorkItems ?? []
            : [];
        return await new ReviewWorkItems(workItems).ContextAsync(linked.Count > 0 ? linked : ReviewWorkItems.FromBranch(session.HeadRef), ct).ConfigureAwait(false);
    }

    /// <summary>What to review: the pinned change, compared like a PR with its target (the review rules' frame).</summary>
    internal static string Prompt(ReviewSession s, string repository, string workItemContext = "")
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
            "There are no PR threads to read here. " +
            "End with the review-findings block as usual; the developer keeps, edits or drops each finding on the page." +
            GuideRequest +
            (s.Focus is { Length: > 0 } focus ? $" Focus on: {focus}." : string.Empty) +
            (s.Instructions is { Length: > 0 } instructions ? $"\n\nInstructions from {s.CreatedBy}: {instructions}" : string.Empty) +
            workItemContext;
    }

    /// <summary>
    /// The guided review (docs/architect/review-sessions.md §2a): after the findings, a guide that walks the developer through
    /// the change. It explains; it doesn't add findings.
    /// </summary>
    internal const string GuideRequest =
        "\n\nThen guide the developer through the change, for someone who hasn't seen this code: after the review-findings block, " +
        "add a fenced block\n```review-guide\n{\"overview\":\"what the change does and how it fits together, two to four sentences\"," +
        "\"chapters\":[{\"title\":\"a few words\",\"why\":\"what this part does and why it matters, one to three sentences\"," +
        "\"files\":[{\"path\":\"src/Foo/Bar.cs\",\"summary\":\"what changed in this file, one sentence\"}]}]," +
        "\"notes\":[{\"file\":\"src/Foo/Bar.cs\",\"line\":42,\"text\":\"what this line or block does and why, one or two sentences\"}]," +
        "\"suggestions\":[{\"file\":\"src/Foo/Bar.cs\",\"line\":42,\"title\":\"at most 12 words\",\"detail\":\"why it's better\"," +
        "\"suggestion\":\"the change, a short code snippet is fine\"}]}\n```\n" +
        "Order the chapters by importance: the heart of the change first, then its consequences, then the glue (wiring, config, " +
        "tests, docs). Put every changed file in exactly one chapter, in the order to read them; at most 10 chapters. Notes explain " +
        "the non-obvious lines (at most 40, on the new side); they aren't problems. Suggestions are optional improvements that don't " +
        "break anything without them (clearer, simpler, safer later; at most 10); never repeat a finding as a suggestion.";

    public void Dispose() => _slots.Dispose();

    private async Task FailAsync(long id, string reason, CancellationToken ct)
    {
        await store.SetStatusAsync(id, ReviewSessionStatus.Failed, reason, null, ct).ConfigureAwait(false);
        await NotifyAsync(id).ConfigureAwait(false);
    }

    /// <summary>A chat that started the review hears how it went (resolved lazily: <see cref="BranchReviews"/> depends on the service that depends on this).</summary>
    private async Task NotifyAsync(long id)
    {
        if (services?.GetService(typeof(BranchReviews)) is not BranchReviews chat || await store.GetAsync(id, CancellationToken.None).ConfigureAwait(false) is not { } session)
        {
            return;
        }

        try
        {
            await chat.NotifyAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a lost chat notice never fails the review
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogNoticeFailed(_logger, ex, id);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Telling the chat about review session {Id} failed")]
    private static partial void LogNoticeFailed(ILogger logger, Exception exception, long id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Review session {Id}: no guide ({Problem}); the page shows the file list")]
    private static partial void LogNoGuide(ILogger logger, long id, string problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Review session {Id} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, long id);
}
