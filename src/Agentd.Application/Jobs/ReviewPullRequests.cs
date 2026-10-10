using System.Globalization;
using System.Text;
using Agentd.Application.Abstractions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// The review loop, run every <see cref="JobOptions.ReviewPollInterval"/> for jobs In Review:
/// a merged PR finishes the job, an abandoned one cancels it, new open reviewer comments, a failed PR build
/// (build validation) or merge conflicts with the target start a fix round in the same session (up to
/// <see cref="JobOptions.MaxFixRounds"/>; each failed run and each conflicted PR head once), and when every
/// thread is resolved "ready to complete" is announced once per round. Returns the jobs changed.
/// </summary>
public sealed record ReviewPullRequests;

public sealed class ReviewPullRequestsHandler(
    IJobRepository jobs,
    IRepositoryRegistry repositories,
    IPullRequestService pullRequests,
    IWorktreeManager worktrees,
    IOutbox outbox,
    ICommandHandler<StartHandoff, Unit> handoff,
    ICommandHandler<RequestCloseOut, Unit> closeOut,
    IOptions<JobOptions> options,
    IAzureDevOpsSearch? search = null) : ICommandHandler<ReviewPullRequests, int>
{
    private const int MaxLogTail = 3000;

    public async Task<Result<int>> Handle(ReviewPullRequests command, CancellationToken cancellationToken)
    {
        var changed = 0;
        foreach (var job in await jobs.ListByStateAsync([JobState.InReview], cancellationToken).ConfigureAwait(false))
        {
            try
            {
                changed += await ReviewAsync(job, cancellationToken).ConfigureAwait(false) ? 1 : 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable PR (or a transient ADO error) must not stop the others; retried next pass.
            }
        }

        return changed;
    }

    private async Task<bool> ReviewAsync(Job job, CancellationToken ct)
    {
        if (job.PullRequest is not { } url || PullRequestId(url.Value) is not { } prId
            || await repositories.GetAsync(job.Repository, ct).ConfigureAwait(false) is not { } repository)
        {
            return false;
        }

        switch (await pullRequests.GetStatusAsync(repository, prId, ct).ConfigureAwait(false))
        {
            case PullRequestStatus.Completed when options.Value.Handoff && job.Handoff == HandoffStatus.None:
                await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Result, $"🎉 **PR merged:** {url.Value}"), ct).ConfigureAwait(false);
                return (await handoff.Handle(new StartHandoff(job.Id), ct).ConfigureAwait(false)).IsSuccess;
            case PullRequestStatus.Completed when job.Handoff == HandoffStatus.Agreed:
                await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Result, "🎓 **Knowledge synced.**"), ct).ConfigureAwait(false);
                if (job.Worktree is { } syncWorktree)
                {
                    await worktrees.RemoveAsync(repository, syncWorktree, ct).ConfigureAwait(false);
                }

                return (await closeOut.Handle(new RequestCloseOut(job.Id), ct).ConfigureAwait(false)).IsSuccess;
            case PullRequestStatus.Completed:
                return await EndAsync(job, repository, job.Merged(), ct).ConfigureAwait(false);
            case PullRequestStatus.Abandoned:
                return await EndAsync(job, repository, job.Cancel("PR abandoned"), ct).ConfigureAwait(false);
        }

        var comments = await pullRequests.ListCommentsAsync(repository, prId, ct).ConfigureAwait(false);
        var unseen = comments.Where(c => !job.Review.SeenCommentIds.Contains(c.CommentId)).ToList();
        var fresh = unseen.Where(c => c.IsOpen).ToList();
        job.MarkCommentsSeen(unseen.Where(c => !c.IsOpen).Select(c => c.CommentId).ToList());
        var (build, conflict) = await SignalsAsync(job, repository, prId, ct).ConfigureAwait(false);

        if (fresh.Count > 0 || build is not null || conflict is not null)
        {
            var what = What(fresh.Count, build, conflict);
            if (job.FixRounds >= options.Value.MaxFixRounds)
            {
                job.MarkCommentsSeen(fresh.Select(c => c.CommentId).ToList());
                job.MarkSignalsHandled(build?.Build.Id, conflict?.SourceCommit);
                await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, string.Create(CultureInfo.InvariantCulture,
                    $"⚠️ **{what}**, but the job already ran {job.FixRounds} fix rounds. Please take over, or `!retry`.")), ct).ConfigureAwait(false);
                return (await jobs.SaveAsync(job, ct).ConfigureAwait(false)).IsSuccess;
            }

            var feedback = fresh.Select(Describe).ToList();
            if (build is not null)
            {
                feedback.Add(DescribeBuild(build));
            }

            if (conflict is not null)
            {
                feedback.Add(DescribeConflicts(conflict));
            }

            job.MarkSignalsHandled(build?.Build.Id, conflict?.SourceCommit);
            if (!job.StartFixRound(feedback, fresh.Select(c => c.CommentId).ToList(), fresh.Select(c => c.ThreadId).Distinct().ToList()).IsSuccess)
            {
                return false;
            }

            var shown = fresh.Select(Describe).ToList();
            if (build is not null)
            {
                shown.Add(string.Create(CultureInfo.InvariantCulture, $"🔴 The PR build failed ({build.Build.Pipeline}, run {build.Build.Id}): {string.Join("; ", build.Failures.Select(f => f.Step))}"));
            }

            if (conflict is not null)
            {
                shown.Add($"⚔️ Merge conflicts with `{conflict.TargetBranch}`: merging it in (never a rebase)");
            }

            await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, $"💬 **{what}**: fix round {job.FixRounds}\n\n" + string.Join("\n", shown.Select(f => "- " + f))), ct).ConfigureAwait(false);
            return (await jobs.SaveAsync(job, ct).ConfigureAwait(false)).IsSuccess;
        }

        if (!comments.Any(c => c.IsOpen) && !job.Review.ReadyAnnounced)
        {
            job.AnnounceReady();
        }

        return (await jobs.SaveAsync(job, ct).ConfigureAwait(false)).IsSuccess;
    }

    /// <summary>The PR build run that failed since the last one handled, and the PR when it has conflicts at a head not handled yet.</summary>
    private async Task<(BuildDetail? Build, PullRequestDetails? Conflict)> SignalsAsync(Job job, Domain.Repositories.Repository repository, int prId, CancellationToken ct)
    {
        BuildDetail? build = null;
        if (search is not null
            && await search.ListBuildsAsync(null, $"refs/pull/{prId}/merge", null, 1, ct).ConfigureAwait(false) is [{ Result: "failed" } newest, ..]
            && newest.Id != job.Review.LastBuildId)
        {
            build = await search.GetBuildAsync(newest.Id, ct).ConfigureAwait(false) ?? new BuildDetail(newest, []);
        }

        var pr = await pullRequests.GetAsync(repository, prId, ct).ConfigureAwait(false);
        var conflict = pr is not null && string.Equals(pr.MergeStatus, "conflicts", StringComparison.OrdinalIgnoreCase) && pr.SourceCommit != job.Review.ConflictCommit ? pr : null;
        return (build, conflict);
    }

    /// <summary>"2 new review comment(s), the PR build failed, merge conflicts".</summary>
    private static string What(int comments, BuildDetail? build, PullRequestDetails? conflict) =>
        string.Join(", ", new[]
        {
            comments > 0 ? string.Create(CultureInfo.InvariantCulture, $"{comments} new review comment(s)") : null,
            build is null ? null : "the PR build failed",
            conflict is null ? null : "merge conflicts",
        }.OfType<string>()) is var text && text.Length > 0 ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    /// <summary>What the agent reads about the failed run: each failed step's errors and the end of its log.</summary>
    public static string DescribeBuild(BuildDetail build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var sb = new StringBuilder().Append(CultureInfo.InvariantCulture, $"The PR build failed ({build.Build.Pipeline}, run {build.Build.Id}). Fix the cause, run the same checks locally, and commit:");
        foreach (var f in build.Failures)
        {
            sb.Append(CultureInfo.InvariantCulture, $"\n- {f.Step}: {string.Join(" | ", f.Issues)}");
            if (f.LogTail is { Length: > 0 } tail)
            {
                sb.Append("\n```\n").Append(tail.Length > MaxLogTail ? tail[^MaxLogTail..] : tail).Append("\n```");
            }
        }

        return sb.ToString();
    }

    /// <summary>What the agent reads about conflicts: merge the target in, never rebase (agentd pushes without force).</summary>
    public static string DescribeConflicts(PullRequestDetails pr)
    {
        ArgumentNullException.ThrowIfNull(pr);
        return $"The PR has merge conflicts with `{pr.TargetBranch}`. Run `git fetch origin {pr.TargetBranch}` and `git merge origin/{pr.TargetBranch}`, " +
            "resolve every conflict keeping both sides' intent, build and test, then commit the merge. Never rebase or rewrite history: agentd pushes without force.";
    }

    private async Task<bool> EndAsync(Job job, Domain.Repositories.Repository repository, Result ended, CancellationToken ct)
    {
        if (!ended.IsSuccess || !(await jobs.SaveAsync(job, ct).ConfigureAwait(false)).IsSuccess)
        {
            return false;
        }

        // No agent process holds the worktree while in review, so it can go now. The branch stays.
        if (job.Worktree is { } worktree)
        {
            await worktrees.RemoveAsync(repository, worktree, ct).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>"Reviewer on AGENTS.md:42 [thread 10]: text" — what the agent and the developer see.</summary>
    public static string Describe(PullRequestComment c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var where = c.FilePath is null ? "" : $" on {c.FilePath.TrimStart('/')}{(c.Line is { } l ? string.Create(CultureInfo.InvariantCulture, $":{l}") : "")}";
        return string.Create(CultureInfo.InvariantCulture, $"{c.Author}{where} [PR thread {c.ThreadId}]: {c.Content.Trim()}");
    }

    /// <summary>The id at the end of <c>…/pullrequest/{id}</c>.</summary>
    public static int? PullRequestId(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return int.TryParse(url.Segments[^1].TrimEnd('/'), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}
