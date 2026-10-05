using System.Text.Json;
using Agentd.Application.Abstractions;
using Agentd.Application.Events;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Queries;

/// <summary>One work item across all its jobs: the header of the work item view.</summary>
public sealed record WorkItemSummary(
    int WorkItemId,
    string Title,
    string Repository,
    IReadOnlyList<JobSummary> Jobs,
    IReadOnlyList<PullRequestLink> PullRequests,
    IReadOnlyList<ConversationLink> Conversations,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastActivityAt);

public sealed record PullRequestLink(long JobId, string Url);

/// <summary>One line of the work item conversation, in either direction.</summary>
/// <param name="At">When it was posted or received.</param>
/// <param name="JobId">The job it belongs to.</param>
/// <param name="Direction"><c>out</c> (agentd posted it) or <c>in</c> (a developer wrote it).</param>
/// <param name="Kind">Out: the message kind (Info, Question, Result, …). In: <c>reply</c> or <c>command</c>.</param>
/// <param name="Text">The message (Markdown for agentd's side).</param>
/// <param name="Provider">Where it was posted, or where a reply came from (null: the Web UI).</param>
/// <param name="From">Who wrote a reply or command.</param>
/// <param name="Status">Delivery status of a posted message (sent, pending, failed, dead).</param>
/// <param name="Error">The last delivery error.</param>
public sealed record ConversationEntry(DateTimeOffset At, long? JobId, string Direction, string Kind, string Text, string? Provider, string? From, string? Status, string? Error);

public sealed record GetWorkItem(WorkItemId WorkItemId);

public sealed record GetWorkItemTimeline(WorkItemId WorkItemId, long? After, long? Before, int Limit);

public sealed record GetWorkItemConversation(WorkItemId WorkItemId);

public sealed class GetWorkItemHandler(IWorkItemHistory history, IConversationStore conversations, JobActivity activity, IClock clock)
    : IQueryHandler<GetWorkItem, WorkItemSummary?>
{
    public async Task<WorkItemSummary?> Handle(GetWorkItem query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var jobs = await history.ListJobsAsync(query.WorkItemId, cancellationToken).ConfigureAwait(false);
        if (jobs.Count == 0)
        {
            return null;
        }

        var links = new List<ConversationLink>();
        foreach (var job in jobs)
        {
            links.AddRange((await conversations.ListByJobAsync(job.Id, cancellationToken).ConfigureAwait(false))
                .Select(c => new ConversationLink(c.Provider.Value, c.Link, c.ClosedAt is null)));
        }

        var now = clock.UtcNow;
        var latest = jobs[^1];
        return new WorkItemSummary(
            query.WorkItemId.Value,
            latest.Title,
            latest.Repository.Value,
            jobs.Select(j => JobViews.Summary(j, activity, now)).ToList(),
            jobs.Where(j => j.PullRequest is not null).GroupBy(j => j.PullRequest!.Value.ToString()).Select(g => new PullRequestLink(g.First().Id.Value, g.Key)).ToList(),
            links.DistinctBy(l => (l.Provider, l.Link)).ToList(),
            jobs[0].CreatedAt,
            jobs.Max(j => j.UpdatedAt));
    }
}

/// <summary>The work item's events, paged like a job's (after → live catch-up, before → load earlier).</summary>
public sealed class GetWorkItemTimelineHandler(IWorkItemHistory history) : IQueryHandler<GetWorkItemTimeline, EventPage>
{
    public async Task<EventPage> Handle(GetWorkItemTimeline query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var page = await history.ReadEventsAsync(query.WorkItemId, query.After, query.Before, query.Limit + 1, cancellationToken).ConfigureAwait(false);
        var hasMore = page.Count > query.Limit;
        var trimmed = hasMore ? (query.Before is null ? page.Take(query.Limit) : page.Skip(1)).ToList() : page.ToList();
        return new EventPage(trimmed, trimmed.Count > 0 ? trimmed[0].Seq : null, trimmed.Count > 0 ? trimmed[^1].Seq : null, hasMore);
    }
}

/// <summary>
/// The chat as it happened, both directions: agentd's posts (outbox rows, with delivery status) and the
/// developers' replies and commands (from the <c>DeveloperReplied</c> and <c>chat.command</c> events).
/// </summary>
public sealed class GetWorkItemConversationHandler(IWorkItemHistory history) : IQueryHandler<GetWorkItemConversation, IReadOnlyList<ConversationEntry>>
{
    public const int MaxPosted = 2000;
    private const int EventPage = 1000;
    private const int MaxEventPages = 50;

    public async Task<IReadOnlyList<ConversationEntry>> Handle(GetWorkItemConversation query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var entries = (await history.ListPostedAsync(query.WorkItemId, MaxPosted, cancellationToken).ConfigureAwait(false))
            .Select(m => new ConversationEntry(m.At, m.JobId, "out", m.Kind, m.Markdown, m.Provider, null, m.Status, m.LastError))
            .ToList();

        long after = 0;
        for (var i = 0; i < MaxEventPages; i++)
        {
            var events = await history.ReadEventsAsync(query.WorkItemId, after, null, EventPage, cancellationToken).ConfigureAwait(false);
            entries.AddRange(events.Select(Incoming).OfType<ConversationEntry>());
            if (events.Count < EventPage)
            {
                break;
            }

            after = events[^1].Seq;
        }

        return entries.OrderBy(e => e.At).ToList();
    }

    private static ConversationEntry? Incoming(AgentEventDto e)
    {
        string? Text(string name) => e.Payload.ValueKind == JsonValueKind.Object && e.Payload.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.Object && v.TryGetProperty("value", out var inner) ? inner.GetString() : v.ValueKind == JsonValueKind.String ? v.GetString() : null
            : null;

        return e.Type switch
        {
            "DeveloperReplied" => new ConversationEntry(e.Ts, e.JobId, "in", "reply", Text("reply") ?? string.Empty, Text("via"), Text("from"), null, null),
            ChatCommands.CommandEventType => new ConversationEntry(e.Ts, e.JobId, "in", "command", Text("text") ?? string.Empty, Text("provider"), Text("user"), null, null),
            _ => null,
        };
    }
}
