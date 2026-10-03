using System.Globalization;
using Agentd.Application.Abstractions;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// The review loop, run every <see cref="JobOptions.ReviewPollInterval"/> for jobs In Review:
/// a merged PR finishes the job, an abandoned one cancels it, new open reviewer comments start a fix
/// round in the same Claude session (up to <see cref="JobOptions.MaxFixRounds"/>), and when every
/// thread is resolved "ready to complete" is announced once per round. Returns the jobs changed.
/// </summary>
public sealed record ReviewPullRequests;

public sealed class ReviewPullRequestsHandler(
    IJobRepository jobs,
    IRepositoryRegistry repositories,
    IPullRequestService pullRequests,
    IWorktreeManager worktrees,
    IOutbox outbox,
    IOptions<JobOptions> options) : ICommandHandler<ReviewPullRequests, int>
{
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
            case PullRequestStatus.Completed:
                return await EndAsync(job, repository, job.Merged(), ct).ConfigureAwait(false);
            case PullRequestStatus.Abandoned:
                return await EndAsync(job, repository, job.Cancel("PR abandoned"), ct).ConfigureAwait(false);
        }

        var comments = await pullRequests.ListCommentsAsync(repository, prId, ct).ConfigureAwait(false);
        var unseen = comments.Where(c => !job.Review.SeenCommentIds.Contains(c.CommentId)).ToList();
        var fresh = unseen.Where(c => c.IsOpen).ToList();
        job.MarkCommentsSeen(unseen.Where(c => !c.IsOpen).Select(c => c.CommentId).ToList());

        if (fresh.Count > 0)
        {
            if (job.FixRounds >= options.Value.MaxFixRounds)
            {
                job.MarkCommentsSeen(fresh.Select(c => c.CommentId).ToList());
                await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, string.Create(CultureInfo.InvariantCulture,
                    $"⚠️ **{fresh.Count} new review comment(s)**, but the job already ran {job.FixRounds} fix rounds. Please take over, or `!retry`.")), ct).ConfigureAwait(false);
                return (await jobs.SaveAsync(job, ct).ConfigureAwait(false)).IsSuccess;
            }

            var feedback = fresh.Select(Describe).ToList();
            if (!job.StartFixRound(feedback, fresh.Select(c => c.CommentId).ToList(), fresh.Select(c => c.ThreadId).Distinct().ToList()).IsSuccess)
            {
                return false;
            }

            await outbox.TryEnqueueAsync(job.Id, new OutboundMessage(MessageKind.Info, "💬 **Review comments**\n\n" + string.Join("\n", feedback.Select(f => "- " + f))), ct).ConfigureAwait(false);
            return (await jobs.SaveAsync(job, ct).ConfigureAwait(false)).IsSuccess;
        }

        if (!comments.Any(c => c.IsOpen) && !job.Review.ReadyAnnounced)
        {
            job.AnnounceReady();
        }

        return (await jobs.SaveAsync(job, ct).ConfigureAwait(false)).IsSuccess;
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
