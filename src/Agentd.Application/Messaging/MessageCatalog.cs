using System.Globalization;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;

namespace Agentd.Application.Messaging;

/// <summary>
/// The messages agentd posts in a job's conversation. Work item, agent and developer text is passed
/// through as data; escaping it for the platform is the provider renderer's job.
/// </summary>
public static class MessageCatalog
{
    public static OutboundMessage Starter(Job job, WorkItemDetails item, Uri? workItemLink)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(item);
        var title = workItemLink is null ? $"#{item.Id} {item.Title}" : $"[#{item.Id} {item.Title}]({workItemLink})";
        return new(MessageKind.Info, string.Create(CultureInfo.InvariantCulture,
            $"**agentd** is working on {title}\n\nRepository `{job.Repository}`, branch `{job.Branch}`. I'll post progress and questions here; reply in this thread to talk to the agent."));
    }

    public static OutboundMessage Progress(string text) => new(MessageKind.Progress, text);

    /// <summary>agentd's start steps: the work item fetched, the worktree ready, the agent started.</summary>
    public static OutboundMessage Started(Job job, WorkItemDetails item, string baseBranch)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(item);
        var criteria = item.AcceptanceCriteria is { Length: > 0 } ac
            ? $"{ac.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} acceptance criteria line(s)"
            : "no acceptance criteria";
        var session = job.Session?.Value.ToString()[..8] ?? "?";
        var run = job.Attempt > 1 || job.ResumeCount > 0 ? $" (attempt {job.Attempt})" : "";
        return new(MessageKind.Info, string.Create(CultureInfo.InvariantCulture,
            $"📄 **Work item #{item.Id} fetched:** {item.Title} ({item.State}; {criteria})\n🌿 **Worktree ready:** `{job.Branch}` from `{baseBranch}`\n🤖 **Agent started**{run}, session `{session}`"));
    }

    public static OutboundMessage Resumed(int messages) =>
        new(MessageKind.Info, string.Create(CultureInfo.InvariantCulture, $"🔁 **Agent resumed** with {messages} message(s) from you."));

    /// <summary>"📊 Actual: 34 min vs ~25 min estimated; usage +18% of the 5-hour window (est. 15%)".</summary>
    public static OutboundMessage ActualVsEstimate(PlanEstimate estimate, DateTimeOffset now, double? usageNow)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var minutes = (int)Math.Round((now - (estimate.ApprovedAt ?? estimate.SubmittedAt)).TotalMinutes);
        var usage = usageNow is { } u && estimate.UsageAtPlan is { } p
            ? string.Create(CultureInfo.InvariantCulture, $"usage +{Math.Max(0, Math.Round((u - p) * 100)):0}% of the 5-hour window (est. {estimate.UsagePercent}%)")
            : string.Create(CultureInfo.InvariantCulture, $"usage not reported (est. {estimate.UsagePercent}%)");
        return new(MessageKind.Info, string.Create(CultureInfo.InvariantCulture, $"📊 **Actual:** {minutes} min vs ~{estimate.Minutes} min estimated; {usage}."));
    }

    public static OutboundMessage Pushed(string branch) => new(MessageKind.Info, $"📤 **Pushed** `{branch}`.");

    /// <summary>The agent finished again, but the job's PR was already merged: nothing is published and no new PR is opened.</summary>
    public static OutboundMessage MergedBeforePublish(Uri pr, string branch) => new(MessageKind.Result,
        $"✅ **The PR was already merged** ({pr}), so I didn't push or open another one. Anything committed after the merge stays on " +
        $"`{branch}` only.\n\nThis thread stays open: ask me about the work and I'll answer (no more code changes here). For new changes, " +
        "use `!run <work item id>` or create a work item.");

    /// <summary>The job's PR was abandoned while the agent worked: no new PR; the developer decides what's next.</summary>
    public static OutboundMessage AbandonedBeforePublish(Uri pr) => new(MessageKind.Info,
        $"⚠️ **The PR was abandoned** ({pr}), so I didn't open a new one and stopped this job. If you want the changes after all, " +
        "use `!retry` (or `!run <work item id>` for a fresh start).");

    public static OutboundMessage Question(string question, IReadOnlyList<MessageOption>? options) =>
        new(MessageKind.Question, $"**Question from the agent**\n\n{question}", options);

    /// <summary>A developer's reply, mirrored into the job's other conversations.</summary>
    public static OutboundMessage Mirrored(string from, string providerName, string text) =>
        new(MessageKind.Info, $"**{from}** (via {providerName}):\n\n{text}");

    public static OutboundMessage PullRequestReady(Uri url, string? summary) =>
        new(MessageKind.Result, string.IsNullOrWhiteSpace(summary)
            ? $"**Pull request ready:** {url}"
            : $"**Pull request ready:** {url}\n\n{summary}");

    public static OutboundMessage Failed(string reason) => new(MessageKind.Error, $"**The job failed.**\n\n{reason}");

    public static OutboundMessage Cancelled(string by) => new(MessageKind.Info, $"The job was cancelled by {by}.");

    public static OutboundMessage Deferred(DateTimeOffset? until, string reason) =>
        new(MessageKind.Info, until is null
            ? $"Paused: {reason}"
            : string.Create(CultureInfo.InvariantCulture, $"Paused until {until:yyyy-MM-dd HH:mm} UTC: {reason}"));
}
