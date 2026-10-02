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
