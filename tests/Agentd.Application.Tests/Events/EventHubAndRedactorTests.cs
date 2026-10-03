using System.Text.Json;
using Agentd.Application.Events;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Application.Tests.Events;

[TestClass]
public sealed class EventHubAndRedactorTests
{
    [TestMethod]
    public async Task Subscribers_get_their_jobs_events_and_the_all_stream_gets_summaries()
    {
        var hub = new EventHub(NullLogger<EventHub>.Instance);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var job7 = Collect(hub, 7, stop.Token);
        var all = Collect(hub, null, stop.Token);
        await WaitFor(() => hub.SubscriberCount == 2);

        hub.Publish(Event(1, 7, "phase.set"));
        hub.Publish(Event(2, 7, "agent.text"));
        hub.Publish(Event(3, 8, "phase.set"));
        await Task.Delay(100);
        await stop.CancelAsync();

        CollectionAssert.AreEqual(new long[] { 1, 2 }, (await job7).Select(e => e.Seq).ToArray());
        CollectionAssert.AreEqual(new long[] { 1, 3 }, (await all).Select(e => e.Seq).ToArray(), "no agent.* in the all stream");
    }

    [TestMethod]
    public async Task A_slow_subscriber_keeps_the_newest_events()
    {
        var hub = new EventHub(NullLogger<EventHub>.Instance);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var enumerator = hub.Subscribe(7, stop.Token).GetAsyncEnumerator(stop.Token);
        var first = enumerator.MoveNextAsync();   // registers the subscriber
        await WaitFor(() => hub.SubscriberCount == 1);

        for (var seq = 1; seq <= EventHub.BufferSize + 10; seq++)
        {
            hub.Publish(Event(seq, 7, "agent.text"));
        }

        Assert.IsTrue(await first);
        var seen = new List<long> { enumerator.Current.Seq };
        while (seen[^1] < EventHub.BufferSize + 10 && await enumerator.MoveNextAsync())
        {
            seen.Add(enumerator.Current.Seq);
        }

        Assert.AreEqual(EventHub.BufferSize + 10, seen[^1], "the newest event arrives");
        Assert.IsLessThanOrEqualTo(EventHub.BufferSize + 1, seen.Count, "the oldest ones were dropped");
        await enumerator.DisposeAsync();
    }

    [TestMethod]
    [DataRow("Authorization: Bearer abcdefghijklmnopqrstuvwxyz012345", "Authorization: Bearer «redacted»")]
    [DataRow("Server=db;Password=hunter2;Database=x", "Server=db;Password=«redacted»;Database=x")]
    [DataRow("{\"apiKey\": \"sk-live-1234567890\"}", "{\"apiKey\": \"«redacted»\"}")]
    [DataRow("token = 'abcdef123456'", "token = '«redacted»'")]
    [DataRow("key AKIAABCDEFGHIJKLMNOP used", "key «redacted» used")]
    [DataRow("ghp_abcdefghijklmnopqrstuvwxyz0123", "«redacted»")]
    [DataRow("ANTHROPIC_API_KEY=sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "ANTHROPIC_API_KEY=«redacted»")]
    [DataRow("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----", "«redacted»")]
    [DataRow("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U", "«redacted»")]
    [DataRow("nothing secret here: token count = 12", "nothing secret here: token count = 12")]
    public void Credentials_are_redacted(string input, string expected) =>
        Assert.AreEqual(expected, SecretRedactor.Redact(input));

    private static AgentEventDto Event(long seq, long job, string type) =>
        new(seq, job, DateTimeOffset.UnixEpoch, type, JsonDocument.Parse("{}").RootElement.Clone());

    private static async Task<List<AgentEventDto>> Collect(EventHub hub, long? job, CancellationToken ct)
    {
        var events = new List<AgentEventDto>();
        try
        {
            await foreach (var e in hub.Subscribe(job, ct))
            {
                events.Add(e);
            }
        }
        catch (OperationCanceledException)
        {
            // Done.
        }

        return events;
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
