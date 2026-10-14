using Agentd.Application.Ideas;
using Agentd.Application.Inbox;
using Agentd.Application.Incidents;
using Agentd.Application.Messaging;
using Agentd.Application.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Tests.Incidents;

[TestClass]
public sealed class IncidentInvestigatorTests
{
    private const string OrdersId = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Insights/components/orders-ai";
    private readonly FakeClock _clock = new();
    private readonly FakeTelemetry _telemetry = new();
    private readonly FakeIncidentStore _store = new();
    private readonly FakeWorktrees _worktrees = new();
    private readonly FakeChat _chat = new("discord");
    private readonly FakeInbox _inbox = new();
    private readonly Agent _agent = new();
    private readonly IncidentMonitorOptions _options = new() { Enabled = true };

    public IncidentInvestigatorTests()
    {
        _options.Apps.Add(new MonitoredApp { Name = "orders-api", ResourceId = OrdersId, Repository = "sysmin" });
    }

    [TestMethod]
    public async Task It_reads_the_latest_occurrences_runs_a_read_only_turn_in_the_repo_and_posts_the_analysis_redacted()
    {
        var incident = await IncidentAsync();
        _telemetry.Occurrences.Add(new ErrorOccurrence(_clock.UtcNow, "Object reference not set", "System.NullReferenceException\n   at Orders.Get() in /src/Orders.cs:line 42",
            "GET /orders", "op-9", "https://orders/api/orders/7", "500", "{\"tenant\":\"acme\",\"conn\":\"Password=hunter2;\"}"));
        _agent.Reply = "**Likely cause** `src/Orders.cs:42` dereferences a null customer. Token: Bearer abcdefghijklmnopqrstuvwxyz0123456789";

        await Investigator().InvestigateAsync(incident.Id, default);

        var turn = _agent.Turns.Single();
        Assert.AreEqual((ThreadTurnKind.Incident, incident.Id, false), (turn.Kind, turn.IdeaId, turn.Resume));
        Assert.AreEqual("/home/agentd/.agentd/worktrees/sysmin/incident-1", turn.Worktree);
        StringAssert.Contains(turn.Prompt, "at Orders.Get() in /src/Orders.cs:line 42");
        StringAssert.Contains(turn.Prompt, "Request: https://orders/api/orders/7 → 500");
        StringAssert.Contains(turn.Prompt, "repository `sysmin` at `develop`");
        Assert.IsFalse(turn.Prompt.Contains("hunter2", StringComparison.Ordinal), "telemetry is redacted before the agent sees it");

        var row = _store.Rows.Single();
        Assert.AreEqual((IncidentStatus.Open, turn.Session), (row.Status, row.Session));
        StringAssert.Contains(row.Analysis, "src/Orders.cs:42");
        Assert.IsFalse(row.Analysis!.Contains("abcdefghijklmnopqrstuvwxyz0123456789", StringComparison.Ordinal), "the analysis is redacted too");
        StringAssert.Contains(_chat.SentText.Single(), "🔎 **Investigation of incident #1**");
        Assert.AreEqual((InboxSources.Incident, 1L), (_inbox.Entries.Single().Source, _inbox.Entries.Single().SourceId));
    }

    [TestMethod]
    public async Task Without_telemetry_access_it_still_investigates_from_the_recorded_sample()
    {
        var incident = await IncidentAsync();
        _telemetry.OccurrencesFail = true;
        _agent.Reply = "Cause found.";

        await Investigator().InvestigateAsync(incident.Id, default);

        StringAssert.Contains(_agent.Turns.Single().Prompt, "agentd couldn't read the occurrences");
        StringAssert.Contains(_agent.Turns.Single().Prompt, "at Orders.Get()");
        Assert.AreEqual("Cause found.", _store.Rows.Single().Analysis);
    }

    [TestMethod]
    public async Task An_unanswered_turn_says_so_and_leaves_the_incident_open_without_an_analysis()
    {
        var incident = await IncidentAsync();
        _agent.Reply = null;

        await Investigator().InvestigateAsync(incident.Id, default);

        Assert.AreEqual((IncidentStatus.Open, (string?)null), (_store.Rows.Single().Status, _store.Rows.Single().Analysis));
        StringAssert.Contains(_chat.SentText.Single(), "I couldn't finish investigating incident #1 (exit 1)");
    }

    [TestMethod]
    public async Task A_failing_checkout_reopens_the_incident_and_says_so()
    {
        var incident = await IncidentAsync();
        _worktrees.NextCheckoutFails = new InvalidOperationException("fetch failed");

        await Investigator().InvestigateAsync(incident.Id, default);

        Assert.AreEqual(IncidentStatus.Open, _store.Rows.Single().Status, "never stuck in Investigating");
        StringAssert.Contains(_chat.SentText.Single(), "I couldn't investigate incident #1 (InvalidOperationException)");
        Assert.IsEmpty(_agent.Turns);
    }

    [TestMethod]
    public async Task Only_an_open_incident_is_investigated_and_an_unregistered_repository_is_named()
    {
        var ignored = await IncidentAsync();
        _store.Rows[0] = _store.Rows[0] with { Status = IncidentStatus.Ignored };
        await Investigator().InvestigateAsync(ignored.Id, default);
        Assert.IsEmpty(_agent.Turns);
        Assert.IsEmpty(_chat.SentText);

        _store.Rows[0] = _store.Rows[0] with { Status = IncidentStatus.Open };
        _options.Apps[0].Repository = "billing";
        await Investigator().InvestigateAsync(ignored.Id, default);

        Assert.IsEmpty(_agent.Turns);
        StringAssert.Contains(_chat.SentText.Single(), "repository `billing` isn't registered");
        Assert.AreEqual(IncidentStatus.Open, _store.Rows[0].Status);
    }

    [TestMethod]
    public async Task A_new_incident_from_the_monitor_is_investigated_in_the_background()
    {
        _telemetry.Groups["orders-api"] = [new ErrorGroup("ex|p1|GET /orders", ErrorKinds.Exception, "NullReferenceException", "boom", "GET /orders", null, 5, 1,
            _clock.UtcNow.AddMinutes(-5), _clock.UtcNow, new ErrorSample("at Orders.Get()", null, "op-1"))];
        _agent.Reply = "Cause found.";
        using var investigator = Investigator();
        var messaging = Messaging();

        await new IncidentMonitor(Options.Create(_options), _telemetry, _store, new MessagingProviderRegistry([_chat], Options.Create(messaging)), Options.Create(messaging),
            _clock, NullLogger<IncidentMonitor>.Instance, _inbox, investigator).PassAsync(default);

        for (var i = 0; i < 200 && _store.Rows.Single().Analysis is null; i++)
        {
            await Task.Delay(10);
        }

        Assert.AreEqual("Cause found.", _store.Rows.Single().Analysis);
        Assert.HasCount(1, _agent.Turns);
    }

    private IncidentInvestigator Investigator()
    {
        var messaging = Messaging();
        return new IncidentInvestigator(_store, _telemetry, new FakeRegistry(), _worktrees, _agent, new MessagingProviderRegistry([_chat], Options.Create(messaging)),
            Options.Create(_options), NullLogger<IncidentInvestigator>.Instance, _inbox);
    }

    private static MessagingOptions Messaging()
    {
        var messaging = new MessagingOptions();
        messaging.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        return messaging;
    }

    private async Task<Incident> IncidentAsync()
    {
        var group = new ErrorGroup("ex|p1|GET /orders", ErrorKinds.Exception, "NullReferenceException", "Object reference not set", "GET /orders", null, 5, 2,
            _clock.UtcNow.AddMinutes(-10), _clock.UtcNow, new ErrorSample("System.NullReferenceException\n   at Orders.Get()", null, "op-1"));
        var recorded = (await _store.RecordAsync("orders-api", group, 1, default))!;
        await _store.SetThreadAsync(recorded.Id, "discord", "thread-9", null, default);
        return (await _store.GetAsync(recorded.Id, default))!;
    }

    private sealed class Agent : IBrainstormAgent
    {
        public string? Reply { get; set; } = "ok";

        public List<BrainstormTurn> Turns { get; } = [];

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            lock (Turns)
            {
                Turns.Add(turn);
            }

            return Task.FromResult(new BrainstormReply(Reply, null, Reply is null ? "exit 1" : null));
        }
    }
}
