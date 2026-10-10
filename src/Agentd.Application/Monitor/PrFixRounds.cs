using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Agentd.Application.Ideas;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Monitor;

/// <summary>
/// A fix round on a watched PR, with approval (docs/architect/pr-reviewer-and-monitor.md §2.0). In the background, an agent
/// edits a checkout of the PR's head (for conflicts, after merging the target branch: never a rebase) and agentd commits;
/// the 👀 thread then asks <b>1</b> push · <b>2</b> discard. Pushing is never forced (a branch that moved on refuses it),
/// replies "Fixed in …" on the comment threads it addressed and leaves a short note on the PR. A prepared round nobody
/// answers is discarded after <see cref="Expiry"/>. One round prepares at a time; answers for one PR are serialized.
/// </summary>
public sealed partial class PrFixRounds(
    IPrWatchStore watches,
    IRepositoryRegistry repositories,
    IWorktreeManager worktrees,
    IBrainstormAgent agent,
    IPullRequestService pullRequests,
    IMessagingProviderRegistry providers,
    IClock clock,
    IOptions<JobOptions>? jobs = null,
    ILogger<PrFixRounds>? logger = null) : IPrFixRounds, IDisposable
{
    public static readonly TimeSpan Expiry = TimeSpan.FromHours(24);
    private const int MaxLogTail = 3000;

    private readonly SemaphoreSlim _slots = new(1, 1);
    private readonly ConcurrentDictionary<long, Task> _preparing = new();
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _answers = new();
    private readonly ILogger _logger = logger ?? NullLogger<PrFixRounds>.Instance;

    public bool IsBusy(long watchId) => _preparing.ContainsKey(watchId);

    public Task Start(PrFixRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return _preparing.GetOrAdd(request.Watch.Id, id => Task.Run(async () =>
        {
            try
            {
                await _slots.WaitAsync().ConfigureAwait(false);
                try
                {
                    await PrepareAsync(request, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _slots.Release();
                }
            }
            finally
            {
                _preparing.TryRemove(id, out Task? _);
            }
        }));
    }

    public async Task PrepareAsync(PrFixRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (w, repo, pr) = (request.Watch, request.Repository, request.PullRequest);
        string? path = null;
        try
        {
            await Post(w, $"🔧 Preparing a fix for PR !{w.PullRequestId}…", ct).ConfigureAwait(false);
            path = await worktrees.CheckoutCommitAsync(repo, $"pr-fix-{w.Id}", pr.SourceCommit, ct).ConfigureAwait(false);
            var conflicted = request.Conflicts ? await worktrees.MergeAsync(path, $"origin/{pr.TargetBranch}", ct).ConfigureAwait(false) : [];
            var reply = await agent.RunAsync(new BrainstormTurn(w.Id, path, Guid.NewGuid(), false, Prompt(request, conflicted), Model(), Effort(), ThreadTurnKind.ReviewFix), ct)
                .ConfigureAwait(false);
            if (reply.UsageLimitedUntil is not null || reply.Text is null)
            {
                await Give(w, reply.UsageLimitedUntil is { } until
                    ? string.Create(CultureInfo.InvariantCulture, $"the Claude usage limit is reached until {until:HH:mm} UTC")
                    : $"the fixer didn't answer ({reply.Error ?? "no reply"})", ct).ConfigureAwait(false);
                return;
            }

            if (await worktrees.ConflictMarkersAsync(path, conflicted, ct).ConfigureAwait(false) is { Count: > 0 } left)
            {
                await Give(w, $"conflict markers are still in {string.Join(", ", left.Select(f => $"`{f}`"))}", ct).ConfigureAwait(false);
                return;
            }

            var commit = await worktrees.CommitAllAsync(path, Message(request), ct).ConfigureAwait(false);
            if (commit is null)
            {
                await Post(w, $"🤷 PR !{w.PullRequestId}: the fixer changed nothing. It said: {Clip(reply.Text)}", ct).ConfigureAwait(false);
                return;
            }

            var diff = await worktrees.DiffCommitsAsync(repo, pr.SourceCommit, commit, 1, ct).ConfigureAwait(false);
            var pending = new PendingFix(path, commit, Clip(reply.Text), [.. request.Comments.Select(c => c.ThreadId).Distinct()], clock.UtcNow + Expiry);
            await Save(w.Id, pending, ct).ConfigureAwait(false);
            path = null;   // kept for push / discard
            var files = diff.Files.Count == 0 ? string.Empty : $"\nFiles: {string.Join(", ", diff.Files.Take(10).Select(f => $"`{f}`"))}{(diff.Files.Count > 10 ? $" and {diff.Files.Count - 10} more" : string.Empty)}";
            await Post(w, $"🔧 **Fix ready for PR !{w.PullRequestId}** ({Short(commit)}): {pending.Summary}{files}\n\n" +
                $"**1** push to `{pr.SourceBranch}` · **2** discard. (Discarded on its own after {Expiry.TotalHours:0} hours.)", ct).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // any failure is this round's, said in the thread
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFailed(_logger, ex, w.PullRequestId);
            await Give(w, Clip(ex.Message), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            if (path is not null)
            {
                await worktrees.RemoveAsync(repo, new WorktreePath(path), CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    public async Task<string> PushAsync(PrWatch watch, string by, CancellationToken cancellationToken)
    {
        var ct = cancellationToken;
        ArgumentNullException.ThrowIfNull(watch);
        return await Answer(watch.Id, async (w, pending, repo) =>
        {
            var pr = await pullRequests.GetAsync(repo, w.PullRequestId, ct).ConfigureAwait(false);
            if (pr is not { Status: PullRequestStatus.Active })
            {
                return $"PR !{w.PullRequestId} isn't active anymore: nothing pushed, the fix is discarded.";
            }

            try
            {
                await worktrees.PushHeadAsync(pending.Worktree, pr.SourceBranch, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return $"⚠️ The push to `{pr.SourceBranch}` was refused ({Clip(ex.Message)}): usually someone pushed since. The fix is discarded; the next change starts a new round.";
            }

            var sha = Short(pending.Commit);
            foreach (var thread in pending.Threads)
            {
                await BestEffort(async () =>
                {
                    await pullRequests.ReplyAsync(repo, w.PullRequestId, thread, $"Fixed in {sha}.", ct).ConfigureAwait(false);
                    await pullRequests.SetThreadStatusAsync(repo, w.PullRequestId, thread, PullRequestThreadStatus.Fixed, ct).ConfigureAwait(false);
                }, w.PullRequestId).ConfigureAwait(false);
            }

            await BestEffort(() => pullRequests.CreateThreadAsync(repo, w.PullRequestId,
                $"🔧 Pushed {sha} (PR Monitor fix round {w.FixRounds}, approved by {by} in chat): {pending.Summary}", null, null, ct), w.PullRequestId).ConfigureAwait(false);
            return $"✅ Pushed {sha} to `{pr.SourceBranch}`. I keep watching PR !{w.PullRequestId}.";
        }, ct).ConfigureAwait(false);
    }

    public async Task<string> DiscardAsync(PrWatch watch, string why, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(watch);
        return await Answer(watch.Id, (w, _, _) => Task.FromResult($"🗑️ Discarded the fix for PR !{w.PullRequestId} ({why})."), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One answer at a time per PR, on the latest state: the pending fix is cleared and its checkout removed whatever happens.</summary>
    private async Task<string> Answer(long watchId, Func<PrWatch, PendingFix, Domain.Repositories.Repository, Task<string>> action, CancellationToken ct)
    {
        var gate = _answers.GetOrAdd(watchId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await watches.GetAsync(watchId, ct).ConfigureAwait(false) is not { Pending: { } pending } w)
            {
                return "There's no prepared fix waiting (it was already pushed or discarded).";
            }

            var repo = RepositoryName.Create(w.Repository) is { IsSuccess: true } name ? await repositories.GetAsync(name.Value, ct).ConfigureAwait(false) : null;
            string text;
            try
            {
                text = repo is null ? $"The repository '{w.Repository}' is no longer registered: the fix is discarded." : await action(w, pending, repo).ConfigureAwait(false);
            }
            finally
            {
                await Save(watchId, null, CancellationToken.None).ConfigureAwait(false);
                if (repo is not null)
                {
                    await worktrees.RemoveAsync(repo, new WorktreePath(pending.Worktree), CancellationToken.None).ConfigureAwait(false);
                }
            }

            await Post(w, text, ct).ConfigureAwait(false);
            return text;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>The watch as it is now, with <paramref name="pending"/> (the monitor leaves a watch alone while a round prepares or waits).</summary>
    private async Task Save(long watchId, PendingFix? pending, CancellationToken ct)
    {
        if (await watches.GetAsync(watchId, ct).ConfigureAwait(false) is { } w)
        {
            await watches.UpdateAsync(w.Id, w.FixRounds, w.SeenComments, w.LastBuildId, w.SignalAt, pending, ct).ConfigureAwait(false);
        }
    }

    internal static string Prompt(PrFixRequest request, IReadOnlyList<string> conflicted)
    {
        var pr = request.PullRequest;
        var sb = new StringBuilder().Append(CultureInfo.InvariantCulture, $"Fix PR !{pr.Id} \"{pr.Title}\" (`{pr.SourceBranch}` → `{pr.TargetBranch}`). ");
        sb.Append("Edit the files only: agentd commits your edits and asks the team before pushing. Don't commit, push or run agentd yourself. ");
        sb.AppendLine("Keep the change to what's asked below, and end with two or three sentences on what you changed.");
        if (conflicted.Count > 0)
        {
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## Merge conflicts\n`{pr.TargetBranch}` is merged into this checkout. Resolve the conflict markers in: {string.Join(", ", conflicted)}. Keep both sides' intent.");
        }
        else if (request.Conflicts)
        {
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## Merge conflicts\n`{pr.TargetBranch}` is merged into this checkout and merged cleanly; check it still builds.");
        }

        if (request.FailedBuild is { } build)
        {
            sb.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"## The PR build failed ({build.Build.Pipeline}, run {build.Build.Id})");
            foreach (var f in build.Failures)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {f.Step}: {string.Join(" | ", f.Issues)}");
                if (f.LogTail is { Length: > 0 } tail)
                {
                    sb.AppendLine("```").AppendLine(tail.Length > MaxLogTail ? tail[^MaxLogTail..] : tail).AppendLine("```");
                }
            }
        }

        if (request.Comments.Count > 0)
        {
            sb.AppendLine().AppendLine("## Reviewer comments");
            foreach (var c in request.Comments)
            {
                var where = c.FilePath is null ? string.Empty : $" ({c.FilePath}{(c.Line is { } l ? string.Create(CultureInfo.InvariantCulture, $":{l}") : string.Empty)})";
                sb.AppendLine(CultureInfo.InvariantCulture, $"- {c.Author}{where}: {c.Content}");
            }
        }

        return sb.ToString();
    }

    internal static string Message(PrFixRequest request)
    {
        var what = new[]
        {
            request.FailedBuild is null ? null : "the PR build",
            request.Conflicts ? $"conflicts with {request.PullRequest.TargetBranch}" : null,
            request.Comments.Count > 0 ? string.Create(CultureInfo.InvariantCulture, $"{request.Comments.Count} review comment(s)") : null,
        }.OfType<string>();
        return $"fix: {string.Join(", ", what)} (agentd PR Monitor, PR !{request.PullRequest.Id})";
    }

    private string? Model() => StepDefaults() is { Profile: null or "" } s && !string.IsNullOrWhiteSpace(s.Model) ? s.Model.Trim() : null;

    private string? Effort() => StepDefaults() is { Profile: null or "" } s && !string.IsNullOrWhiteSpace(s.Effort) ? s.Effort.Trim().ToLowerInvariant() : null;

    /// <summary>The <c>fix</c> step's model when it's a Claude one (this agent is Claude Code); otherwise Claude's default.</summary>
    private StepModel? StepDefaults() => jobs?.Value.Steps.TryGetValue(JobSteps.Fix, out var s) == true ? s : null;

    private Task Give(PrWatch w, string why, CancellationToken ct) =>
        Post(w, $"⚠️ PR !{w.PullRequestId}: I couldn't prepare a fix: {why}. The next change starts a new round.", ct);

    private async Task Post(PrWatch w, string text, CancellationToken ct) =>
        await providers.Resolve(w.Provider).SendAsync(new ConversationRef(w.Provider, w.ThreadId, w.SpaceId), new OutboundMessage(MessageKind.Info, text), ct).ConfigureAwait(false);

    private async Task BestEffort(Func<Task> action, int pr)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // replies and notes never undo the push
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogNoteFailed(_logger, ex, pr);
        }
    }

    private static string Short(string commit) => commit[..Math.Min(7, commit.Length)];

    private static string Clip(string text)
    {
        var flat = text.Trim().ReplaceLineEndings(" ");
        return flat[..Math.Min(500, flat.Length)];
    }

    public void Dispose()
    {
        _slots.Dispose();
        foreach (var gate in _answers.Values)
        {
            gate.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preparing a fix for watched PR {Pr} failed")]
    private static partial void LogFailed(ILogger logger, Exception exception, int pr);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A reply or note on PR {Pr} couldn't be posted")]
    private static partial void LogNoteFailed(ILogger logger, Exception exception, int pr);
}
