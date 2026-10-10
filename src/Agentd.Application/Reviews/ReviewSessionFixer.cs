using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Agentd.Application.AzureDevOps;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Application.Reviews;

/// <summary>
/// Fix it (docs/architect/review-sessions.md §4): the review's kept findings and comments are fixed on the reviewed branch
/// itself. An agent edits a checkout of the reviewed commit, agentd commits (as agentd) and pushes to the branch (never
/// forced: a branch that moved on refuses it), then reviews the new head: round 2 is a new review, and so on. Its progress
/// is the session's <c>sent_to</c>: <see cref="Running"/>, <c>fix:&lt;next review&gt;:&lt;commit&gt;</c>, <see cref="NoChange"/> or <see cref="Failed"/> (with the reason).
/// </summary>
public sealed partial class ReviewSessionFixer(
    IReviewSessionStore store,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    IPullRequestService pullRequests,
    ReviewSessionService sessions,
    AdoOnBehalf? onBehalf = null,
    AdoActor? actor = null,
    ILogger<ReviewSessionFixer>? logger = null) : IDisposable
{
    public const string Running = "fix:running";
    public const string NoChange = "fix:nochange";
    public const string Failed = "fix:failed";

    private readonly SemaphoreSlim _slots = new(1, 1);
    private readonly ConcurrentDictionary<long, Task> _running = new();
    private readonly ILogger _logger = logger ?? NullLogger<ReviewSessionFixer>.Instance;

    /// <summary>Fixes in the background, one at a time (each edits and pushes a branch).</summary>
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
            _running.TryRemove(key, out Task? _);
        }
    }));

    public async Task RunAsync(long id, CancellationToken ct)
    {
        if (await store.GetAsync(id, ct).ConfigureAwait(false) is not { SentTo: Running, HeadRef: { } branch, HeadCommit: { } head } session)
        {
            return;
        }

        string? path = null;
        Domain.Repositories.Repository? repo = null;
        try
        {
            repo = RepositoryName.Create(session.Repository) is { IsSuccess: true } name ? await repositories.GetAsync(name.Value, ct).ConfigureAwait(false) : null;
            if (repo is null)
            {
                await EndAsync(id, Failed, $"The repository '{session.Repository}' is no longer registered.", ct).ConfigureAwait(false);
                return;
            }

            path = await worktrees.CheckoutCommitAsync(repo, $"review-fix-{id}", head, ct).ConfigureAwait(false);
            var comments = await store.ListCommentsAsync(id, ct).ConfigureAwait(false);
            var feedback = ReviewFeedback.Render($"{session.BaseRef}", session.Summary, session.Findings, comments);
            var reply = await agent.RunAsync(new BrainstormTurn(id, path, Guid.NewGuid(), false, Prompt(branch, feedback), session.Model, session.Effort, ThreadTurnKind.ReviewFix), ct)
                .ConfigureAwait(false);
            if (reply.UsageLimitedUntil is { } until || reply.Text is null)
            {
                await EndAsync(id, Failed, reply.UsageLimitedUntil is { } u
                    ? string.Create(CultureInfo.InvariantCulture, $"The Claude usage limit is reached until {u:HH:mm} UTC. Send Fix it again after that.")
                    : $"The fixer didn't answer ({reply.Error ?? "no reply"}).", ct).ConfigureAwait(false);
                return;
            }

            var kept = ReviewFeedback.Kept(session.Findings);
            var commit = await worktrees.CommitAllAsync(path, Message(id, session.CreatedBy, kept, comments.Count), ct).ConfigureAwait(false);
            if (commit is null)
            {
                await EndAsync(id, NoChange, Clip(reply.Text), ct).ConfigureAwait(false);
                return;
            }

            try
            {
                await worktrees.PushHeadAsync(path, branch, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await EndAsync(id, Failed, $"The push to {branch} was refused ({Clip(ex.Message)}). Usually the branch moved on since the review: review it again.", ct).ConfigureAwait(false);
                return;
            }

            var next = await sessions.StartAsync(session.Target == ReviewTargets.PullRequest
                ? new StartReview(session.Repository, PullRequestId: session.PullRequestId)
                : new StartReview(session.Repository, Branch: branch, Base: session.BaseRef), session.CreatedBy, ct).ConfigureAwait(false);
            var sha = commit[..Math.Min(7, commit.Length)];
            await EndAsync(id, next.IsSuccess ? $"fix:{next.Value!.Id}:{sha}" : $"fix:0:{sha}", next.IsSuccess ? null : next.Error!.Message, ct).ConfigureAwait(false);
            if (session.PullRequestId is { } pr)
            {
                await NoteAsync(repo, pr, session.CreatedBy, $"🔧 Fixed {kept.Count} review finding(s) and {comments.Count} comment(s) in {sha}, from agentd's review page. Reviewing again.", ct).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // any failure is the fix's, shown on the review's page
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(_logger, ex, id);
            await EndAsync(id, Failed, $"Fix it failed: {Clip(ex.Message)}", CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (path is not null && repo is not null)
            {
                await worktrees.RemoveAsync(repo, new WorktreePath(path), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    internal static string Prompt(string branch, string feedback) =>
        $"Fix this review of the branch `{branch}`. agentd will commit your edits and push them to `{branch}`, then review them again.\n\n{feedback}";

    internal static string Message(long id, string by, IReadOnlyList<ReviewFinding> kept, int comments)
    {
        var sb = new StringBuilder($"fix: address review findings (agentd review #{id})\n\n");
        foreach (var f in kept)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {f.Title}{(f.File is null ? string.Empty : $" ({f.File}{(f.Line is { } l ? string.Create(CultureInfo.InvariantCulture, $":{l}") : string.Empty)})")}");
        }

        if (comments > 0)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- {comments} reviewer comment(s)");
        }

        return sb.AppendLine().Append(CultureInfo.InvariantCulture, $"Reviewed by {by} on agentd's review page.").ToString();
    }

    private Task EndAsync(long id, string sentTo, string? reason, CancellationToken ct) =>
        store.SetStatusAsync(id, ReviewSessionStatus.Sent, reason, sentTo, ct);

    /// <summary>A short note on the PR, under the reviewer's name when connected; best effort.</summary>
    private async Task NoteAsync(Domain.Repositories.Repository repo, int pr, string webLogin, string text, CancellationToken ct)
    {
        try
        {
            var person = onBehalf is null || actor is null ? null : await onBehalf.ForWebLoginAsync(webLogin, ct).ConfigureAwait(false);
            using (actor?.Begin(person))
            {
                await pullRequests.CreateThreadAsync(repo, pr, text, null, null, ct).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031 // a missing note never undoes the push
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogNoteFailed(_logger, ex, pr);
        }
    }

    private static string Clip(string text)
    {
        var flat = text.Trim().ReplaceLineEndings(" ");
        return flat[..Math.Min(400, flat.Length)];
    }

    public void Dispose() => _slots.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fix it for review session {Id} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, long id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The fix note on PR {Pr} couldn't be posted")]
    private static partial void LogNoteFailed(ILogger logger, Exception exception, int pr);
}
