using System.Globalization;
using System.Text;
using Agentd.Application.Ports;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Reviews;

/// <summary>
/// The re-check of findings posted to a PR, shared by a chat <c>!review</c> and a review page's Send (review-sessions.md §4):
/// the prompt, then the PR's update (replies and statuses on each finding's thread, threads for new findings, the main
/// message edited in place and resolved when nothing's open), then the one-line summary.
/// </summary>
public static class PostedRechecks
{
    /// <summary>
    /// The re-check prompt: the posted findings, the commits since <paramref name="lastHead"/>, and replies on each
    /// finding's thread since the last check. <paramref name="withDetail"/>: a fresh agent session needs each finding's text.
    /// </summary>
    public static string Prompt(string? lastHead, ReviewResult posted, IReadOnlyList<PullRequestComment> fresh, bool withDetail)
    {
        ArgumentNullException.ThrowIfNull(posted);
        ArgumentNullException.ThrowIfNull(fresh);
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"(agentd) Re-check your posted findings against the PR's current head (they were last checked at {lastHead}; `git diff {lastHead}..HEAD` shows what changed).");
        sb.AppendLine();
        for (var i = 0; i < posted.Findings.Count; i++)
        {
            var f = posted.Findings[i];
            sb.AppendLine(CultureInfo.InvariantCulture, $"#{i + 1} [{f.Status ?? ReviewFindings.Open}] {f.Title} ({f.File}{(f.Line is { } l ? $":{l}" : string.Empty)})");
            if (withDetail && !string.IsNullOrWhiteSpace(f.Detail))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"   {f.Detail.ReplaceLineEndings(" ").Trim()}");
            }

            foreach (var c in fresh.Where(c => c.ThreadId == f.Thread))
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"   reply from {c.Author}: {c.Content.ReplaceLineEndings(" ").Trim()}");
            }
        }

        sb.AppendLine();
        sb.Append("For each finding decide: fixed (the problem is gone in the code: check the code, not just the reply), closed (the author " +
            "convinced you it isn't a problem: say so briefly) or open (still there: reply only with what is still wrong and where, or null " +
            "when there's nothing new to say). Add problems the new commits introduce under \"new\" (breaks or performance only). Then end " +
            "with:\n```review-recheck\n{\"findings\":[{\"n\":1,\"status\":\"fixed\",\"reply\":null}],\"new\":[]}\n```");
        return sb.ToString();
    }

    /// <summary>
    /// Updates the PR for <paramref name="recheck"/> at <paramref name="head"/>: returns the findings with their new statuses
    /// (new ones added) and how many were new. A write that fails is reported to <paramref name="failed"/> and skipped.
    /// </summary>
    public static async Task<(ReviewResult Result, int Added)> ApplyAsync(IPullRequestService pullRequests, Repository repo, int pullRequestId, string? head,
        ReviewResult posted, Recheck recheck, bool rechecks, Action<Exception> failed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pullRequests);
        ArgumentNullException.ThrowIfNull(posted);
        ArgumentNullException.ThrowIfNull(recheck);
        ArgumentNullException.ThrowIfNull(failed);
        var at = Short(head);
        var findings = posted.Findings.ToList();
        foreach (var item in recheck.Findings)
        {
            var f = findings[item.Number - 1];
            var was = f.Status ?? ReviewFindings.Open;
            var (reply, status) = item.Status switch
            {
                ReviewFindings.Fixed when was != ReviewFindings.Fixed => ($"✅ Fixed in {at}.", PullRequestThreadStatus.Fixed),
                ReviewFindings.Closed when was != ReviewFindings.Closed => (string.IsNullOrWhiteSpace(item.Reply) ? "👍 Closing." : item.Reply.Trim(), PullRequestThreadStatus.Closed),
                ReviewFindings.Open => (string.IsNullOrWhiteSpace(item.Reply) ? null : $"Still open in {at}: {item.Reply.Trim()}", was == ReviewFindings.Open ? (PullRequestThreadStatus?)null : PullRequestThreadStatus.Active),
                _ => (null, null),
            };
            findings[item.Number - 1] = f with { Status = item.Status };
            if (f.Thread is not { } thread)
            {
                continue;
            }

            try
            {
                if (reply is not null)
                {
                    await pullRequests.ReplyAsync(repo, pullRequestId, thread, reply, ct).ConfigureAwait(false);
                }

                if (status is { } s)
                {
                    await pullRequests.SetThreadStatusAsync(repo, pullRequestId, thread, s, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed(ex);
            }
        }

        var added = 0;
        foreach (var f in recheck.New ?? [])
        {
            try
            {
                var thread = await pullRequests.CreateThreadAsync(repo, pullRequestId, ReviewFindings.ThreadText(f), f.File, f.Line, ct).ConfigureAwait(false);
                findings.Add(f with { Thread = thread, Status = ReviewFindings.Open });
                added++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed(ex);
            }
        }

        var result = posted with { Findings = findings };
        if (result.MainThread is { } main)
        {
            try
            {
                await pullRequests.UpdateThreadTextAsync(repo, pullRequestId, main, ReviewFindings.MainMessage(result, head, rechecks), ct).ConfigureAwait(false);
                var (before, now) = (ReviewFindings.AllResolved(posted), ReviewFindings.AllResolved(result));
                if (before != now)
                {
                    // Everything fixed or closed: the main message is resolved too; a finding open again reopens it.
                    await pullRequests.SetThreadStatusAsync(repo, pullRequestId, main, now ? PullRequestThreadStatus.Fixed : PullRequestThreadStatus.Active, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed(ex);
            }
        }

        return (result, added);
    }

    /// <summary>"🔄 PR !12 re-checked at abc1234: 1 fixed ✅, 1 open, 1 new."</summary>
    public static string Summary(int pullRequestId, string? head, IReadOnlyList<ReviewFinding> findings, int added)
    {
        ArgumentNullException.ThrowIfNull(findings);
        var open = findings.Count(f => (f.Status ?? ReviewFindings.Open) == ReviewFindings.Open);
        return $"🔄 **PR !{pullRequestId} re-checked at {Short(head)}:** {findings.Count(f => f.Status == ReviewFindings.Fixed)} fixed ✅, {open} open" +
            $"{(findings.Any(f => f.Status == ReviewFindings.Closed) ? $", {findings.Count(f => f.Status == ReviewFindings.Closed)} closed" : string.Empty)}" +
            $"{(added > 0 ? $", {added} new" : string.Empty)}.{(open == 0 ? " Everything is resolved 🎉" : string.Empty)}";
    }

    private static string Short(string? head) => head is { Length: > 0 } h ? h[..Math.Min(7, h.Length)] : "the latest head";
}
