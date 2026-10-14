using Agentd.Application.Inbox;
using Agentd.Application.Incidents;
using Agentd.Application.Messaging;
using Agentd.Application.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Tests.Incidents;

[TestClass]
public sealed class IncidentMonitorTests
{
    private const string OrdersId = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Insights/components/orders-ai";
    private readonly FakeClock _clock = new();
    private readonly FakeTelemetry _telemetry = new();
    private readonly FakeIncidentStore _store = new();
    private readonly FakeChat _chat = new("discord");
    private readonly FakeInbox _inbox = new();
    private readonly IncidentMonitorOptions _options = new() { Enabled = true, MinCount = 3, MaxNewPerPass = 5 };

    public IncidentMonitorTests()
    {
        _options.Apps.Add(new MonitoredApp { Name = "orders-api", ResourceId = OrdersId, Repository = "orders" });
    }

    [TestMethod]
    public async Task A_new_error_group_over_the_threshold_opens_a_thread_and_lands_in_the_Inbox_redacted()
    {
        _telemetry.Groups["orders-api"] = [Group("ex|p1|GET /orders", 5, "Connection failed: Password=hunter2;"), Group("ex|p2|GET /items", 2)];

        var alerts = await Monitor().PassAsync(default);

        Assert.AreEqual(1, alerts);
        Assert.HasCount(1, _store.Rows, "below the threshold isn't an incident");
        Assert.AreEqual("🚨 orders-api: NullReferenceException", _chat.Opened.Single().Name);
        var text = string.Join('\n', _chat.SentText);
        StringAssert.Contains(text, "🚨 **New incident #1** in **orders-api** (exception)");
        StringAssert.Contains(text, "**5** in the last 15 min · 2 user(s) · operation `GET /orders`");
        StringAssert.Contains(text, "at Orders.Get()");
        StringAssert.Contains(text, $"https://portal.azure.com/#resource{OrdersId}/failures");
        Assert.IsFalse(text.Contains("hunter2", StringComparison.Ordinal), "secrets never reach the chat");
        var entry = _inbox.Entries.Single();
        Assert.AreEqual((InboxSources.Incident, 1L), (entry.Source, entry.SourceId));
        Assert.IsFalse(entry.Message.Markdown.Contains("hunter2", StringComparison.Ordinal));
        Assert.AreEqual(("discord", "thread-0"), (_store.Rows[0].Provider, _store.Rows[0].ThreadId));
    }

    [TestMethod]
    public async Task A_known_incident_is_counted_again_quietly_and_a_resolved_one_that_returns_posts_in_its_thread()
    {
        _telemetry.Groups["orders-api"] = [Group("ex|p1|GET /orders", 5)];
        var monitor = Monitor();
        await monitor.PassAsync(default);
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        _telemetry.Groups["orders-api"] = [Group("ex|p1|GET /orders", 1)];

        Assert.AreEqual(0, await monitor.PassAsync(default), "known: counted, no alert, even under the threshold");
        Assert.AreEqual(6L, _store.Rows[0].TotalCount);

        _store.Rows[0] = _store.Rows[0] with { Status = IncidentStatus.Resolved };
        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        Assert.AreEqual(1, await monitor.PassAsync(default));

        Assert.HasCount(1, _chat.Opened, "back in the same thread");
        StringAssert.Contains(_chat.SentText[^1], "🔁 **Incident #1 is back**");
        Assert.AreEqual(IncidentStatus.Open, _store.Rows[0].Status);
    }

    [TestMethod]
    public async Task At_most_MaxNewPerPass_alerts_go_out_and_the_rest_are_recorded()
    {
        _options.MaxNewPerPass = 2;
        _telemetry.Groups["orders-api"] = [Group("ex|a|op", 9), Group("ex|b|op", 8), Group("ex|c|op", 7)];

        Assert.AreEqual(2, await Monitor().PassAsync(default));

        Assert.HasCount(3, _store.Rows);
        Assert.HasCount(2, _chat.Opened);
        Assert.HasCount(2, _inbox.Entries);
    }

    [TestMethod]
    public async Task Without_chat_the_Inbox_has_the_alert()
    {
        _telemetry.Groups["orders-api"] = [Group("ex|p1|GET /orders", 5)];

        Assert.AreEqual(1, await Monitor(chat: false).PassAsync(default));

        Assert.IsEmpty(_chat.Opened);
        StringAssert.Contains(_inbox.Entries.Single().Message.Markdown, "New incident #1");
        Assert.IsNull(_store.Rows[0].ThreadId);
    }

    [TestMethod]
    public async Task An_app_that_cant_be_read_keeps_its_place_and_the_others_go_on()
    {
        _options.Apps.Insert(0, new MonitoredApp { Name = "billing", ResourceId = OrdersId + "2", Repository = "billing" });
        _telemetry.Failing.Add("billing");
        _telemetry.Groups["orders-api"] = [Group("ex|p1|GET /orders", 5)];

        Assert.AreEqual(1, await Monitor().PassAsync(default));

        Assert.IsFalse(_store.Cursors.ContainsKey("billing"), "read again from the same point next pass");
        Assert.AreEqual(_clock.UtcNow, _store.Cursors["orders-api"]);
    }

    [TestMethod]
    public async Task Each_pass_reads_from_where_the_last_ended_the_first_from_FirstWindow_and_never_more_than_a_day()
    {
        var monitor = Monitor();
        var start = _clock.UtcNow;
        await monitor.PassAsync(default);
        _clock.UtcNow = start.AddMinutes(5);
        await monitor.PassAsync(default);
        _clock.UtcNow = start.AddDays(3);
        await monitor.PassAsync(default);

        CollectionAssert.AreEqual(
            new[] { (start.AddMinutes(-15), start), (start, start.AddMinutes(5)), (start.AddDays(2), start.AddDays(3)) },
            _telemetry.Windows.ToArray());
    }

    private IncidentMonitor Monitor(bool chat = true)
    {
        var messaging = new MessagingOptions();
        messaging.Providers["discord"] = new MessagingProviderSettings { Enabled = chat };
        return new IncidentMonitor(Options.Create(_options), _telemetry, _store, new MessagingProviderRegistry([_chat], Options.Create(messaging)), Options.Create(messaging),
            _clock, NullLogger<IncidentMonitor>.Instance, _inbox);
    }

    private ErrorGroup Group(string fingerprint, long count, string? message = "Object reference not set") =>
        new(fingerprint, ErrorKinds.Exception, "NullReferenceException", message, fingerprint.Split('|')[^1], "orders", count, 2,
            _clock.UtcNow.AddMinutes(-10), _clock.UtcNow.AddMinutes(-1), new ErrorSample("System.NullReferenceException\n   at Orders.Get()\n   at Host.Run()", null, "op-1"));
}
