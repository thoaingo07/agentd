using System.Text.Json;
using Agentd.Application.Events;
using Agentd.Application.Ports;
using Agentd.Application.Queries;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Tests.Queries;

[TestClass]
public sealed class WorkItemQueryTests
{
    private static readonly DateTimeOffset s_t0 = DateTimeOffset.Parse("2026-10-03T16:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    [TestMethod]
    public async Task The_conversation_merges_both_directions_in_time_order()
    {
        var history = new History
        {
            Posted = [new PostedMessage(1, 3, "discord", "Question", "sent", "Which endpoint?", s_t0.AddMinutes(1), null), new PostedMessage(2, 3, "discord", "Info", "dead", "📤 Pushed", s_t0.AddMinutes(3), "403")],
            Events =
            [
                Event(10, "agent.text", s_t0, """{"text":"thinking"}"""),
                Event(11, "DeveloperReplied", s_t0.AddMinutes(2), """{"reply":"v2","from":"tngo","via":{"value":"discord"}}"""),
                Event(12, "chat.command", s_t0.AddMinutes(4), """{"name":"status","text":"!status","user":"tngo","provider":"discord"}"""),
            ],
        };

        var entries = await new GetWorkItemConversationHandler(history).Handle(new GetWorkItemConversation(WorkItemId.From(5613)), default);

        CollectionAssert.AreEqual(
            new[] { "out|Question|Which endpoint?|sent", "in|reply|v2|tngo", "out|Info|📤 Pushed|dead", "in|command|!status|tngo" },
            entries.Select(e => $"{e.Direction}|{e.Kind}|{e.Text}|{(e.Direction == "out" ? e.Status : e.From)}").ToArray());
        Assert.AreEqual("discord", entries[1].Provider);
        Assert.AreEqual("403", entries[2].Error);
    }

    [TestMethod]
    public async Task Timeline_pages_report_whether_more_exist()
    {
        var history = new History { Events = Enumerable.Range(1, 10).Select(i => Event(i, "phase.set", s_t0, "{}")).ToList() };
        var handler = new GetWorkItemTimelineHandler(history);

        var first = await handler.Handle(new GetWorkItemTimeline(WorkItemId.From(1), null, null, 4), default);
        var last = await handler.Handle(new GetWorkItemTimeline(WorkItemId.From(1), 8, null, 4), default);

        Assert.AreEqual((1L, 4L, true), (first.OldestSeq!.Value, first.NewestSeq!.Value, first.HasMore));
        Assert.AreEqual((9L, 10L, false), (last.OldestSeq!.Value, last.NewestSeq!.Value, last.HasMore));
    }

    private static AgentEventDto Event(long seq, string type, DateTimeOffset at, string json) => new(seq, 3, at, type, JsonDocument.Parse(json).RootElement.Clone());

    private sealed class History : IWorkItemHistory
    {
        public List<PostedMessage> Posted { get; init; } = [];

        public List<AgentEventDto> Events { get; init; } = [];

        public Task<IReadOnlyList<Job>> ListJobsAsync(WorkItemId workItem, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Job>>([]);

        public Task<IReadOnlyList<AgentEventDto>> ReadEventsAsync(WorkItemId workItem, long? after, long? before, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentEventDto>>(before is { } b
                ? Events.Where(e => e.Seq < b).TakeLast(limit).ToList()
                : Events.Where(e => e.Seq > (after ?? 0)).Take(limit).ToList());

        public Task<IReadOnlyList<PostedMessage>> ListPostedAsync(WorkItemId workItem, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PostedMessage>>(Posted);
    }
}
