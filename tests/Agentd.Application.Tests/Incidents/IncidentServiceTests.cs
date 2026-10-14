using Agentd.Application.Ideas;
using Agentd.Application.Incidents;
using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentd.Application.Tests.Incidents;

[TestClass]
public sealed class IncidentServiceTests : IDisposable
{
    private const string OrdersId = "/subscriptions/s1/resourceGroups/rg/providers/Microsoft.Insights/components/orders-ai";
    private readonly FakeClock _clock = new();
    private readonly FakeTelemetry _telemetry = new();
    private readonly FakeIncidentStore _store = new();
    private readonly FakeChat _chat = new("discord");
    private readonly FakeInbox _inbox = new();
    private readonly FakeWorkItems _workItems = new();
    private readonly Agent _agent = new();
    private readonly IncidentMonitorOptions _options = new() { Enabled = true };
    private IncidentInvestigator? _investigator;

    public IncidentServiceTests()
    {
        _options.Apps.Add(new MonitoredApp { Name = "orders-api", ResourceId = OrdersId, Repository = "sysmin" });
    }

    public void Dispose() => _investigator?.Dispose();

    [TestMethod]
    public async Task Fix_it_files_one_Bug_tagged_for_agentd_in_the_repos_area_with_the_redacted_analysis_and_a_second_names_it()
    {
        var incident = await IncidentAsync(analysis: "**Likely cause** `src/Orders.cs:42`. Conn: Password=hunter2;");
        using var service = Service();

        Assert.AreEqual("filed", await service.HandleAsync(incident, "tngo", canAct: true, "🛠 Fix it", default));
        Assert.AreEqual("filed", await service.HandleAsync(incident, "kelvin", canAct: true, "fix", default));

        var bug = _workItems.Created.Single();
        Assert.AreEqual(("Bug", "[orders-api] NullReferenceException in GET /orders", "Portal\\Platform"), (bug.Type, bug.Title, bug.AreaPath));
        CollectionAssert.AreEqual(new[] { "ai-workflow", "repo:sysmin", "incident" }, bug.Tags.ToArray(), "so agentd's poll picks it up for this repository");
        StringAssert.Contains(bug.Description, "src/Orders.cs:42");
        StringAssert.Contains(bug.Description, "at Orders.Get()");
        StringAssert.Contains(bug.Description, $"https://portal.azure.com/#resource{OrdersId}/failures");
        Assert.IsFalse(bug.Description!.Contains("hunter2", StringComparison.Ordinal));
        Assert.AreEqual((IncidentStatus.Fixing, 9001), (_store.Rows.Single().Status, _store.Rows.Single().WorkItemId));
        StringAssert.Contains(_chat.SentText[0], "🛠 **Filed Bug #9001** (https://dev.azure.com/ermsystem/Portal/_workitems/edit/9001) for tngo");
        StringAssert.Contains(_chat.SentText[1], "Already filed as Bug #9001");
    }

    [TestMethod]
    public async Task Only_Operators_and_Admins_fix_ignore_or_reopen_but_anyone_may_ask()
    {
        var incident = await IncidentAsync();
        using var service = Service();

        Assert.AreEqual("denied", await service.HandleAsync(incident, "guest", canAct: false, "fix it", default));
        Assert.AreEqual("denied", await service.HandleAsync(incident, "guest", canAct: false, "ignore", default));

        Assert.IsEmpty(_workItems.Created);
        Assert.AreEqual(IncidentStatus.Open, _store.Rows.Single().Status);
        StringAssert.Contains(_chat.SentText[0], "Only Operators and Admins can do that, guest");
        Assert.AreEqual("asked", await service.HandleAsync(incident, "guest", canAct: false, "why only on Mondays?", default));
    }

    [TestMethod]
    public async Task Ignore_keeps_it_quiet_and_reopen_watches_it_again()
    {
        var incident = await IncidentAsync();
        using var service = Service();

        Assert.AreEqual("ignored", await service.HandleAsync(incident, "tngo", canAct: true, "🙈 Ignore", default));
        Assert.AreEqual(IncidentStatus.Ignored, _store.Rows.Single().Status);
        StringAssert.Contains(_chat.SentText[^1], "🙈 Ignored by tngo");

        var recurred = await _store.RecordAsync("orders-api", Group(9), 1, default);
        Assert.AreEqual((false, false, IncidentStatus.Ignored), (recurred!.Created, recurred.Reopened, _store.Rows.Single().Status), "counted, no alert");

        Assert.AreEqual("reopened", await service.HandleAsync(incident, "tngo", canAct: true, "reopen", default));
        Assert.AreEqual(IncidentStatus.Open, _store.Rows.Single().Status);
        Assert.AreEqual("unchanged", await service.HandleAsync(incident, "tngo", canAct: true, "reopen", default));
    }

    [TestMethod]
    public async Task A_question_resumes_the_investigations_session_and_investigate_again_reruns_one_a_restart_cut_off()
    {
        var session = Guid.NewGuid();
        var incident = await IncidentAsync(analysis: "Cause found.", session: session);
        using var service = Service();
        _agent.Reply = "Only on Mondays because the batch runs then; see `src/Batch.cs:10`.";

        Assert.AreEqual("asked", await service.HandleAsync(incident, "tngo", canAct: true, "why only on Mondays?", default));
        await WaitAsync(() => _agent.Turns.Count == 1);
        var turn = _agent.Turns.Single();
        Assert.AreEqual((session, true, ThreadTurnKind.Incident), (turn.Session, turn.Resume, turn.Kind));
        Assert.AreEqual("tngo asks: why only on Mondays?", turn.Prompt);
        await WaitAsync(() => _chat.SentText.Count == 1);
        StringAssert.Contains(_chat.SentText.Single(), "src/Batch.cs:10");

        _store.Rows[0] = _store.Rows[0] with { Status = IncidentStatus.Investigating };   // a restart cut it off
        _agent.Reply = "New cause.";
        Assert.AreEqual("investigating", await service.HandleAsync(incident, "tngo", canAct: true, "investigate again", default));
        await WaitAsync(() => _store.Rows[0].Analysis == "New cause.");
        Assert.AreEqual((IncidentStatus.Open, false), (_store.Rows[0].Status, _agent.Turns[^1].Resume));
    }

    [TestMethod]
    public async Task A_fixed_incident_is_resolved_when_its_job_is_done_and_says_so_when_it_comes_back()
    {
        var incident = await IncidentAsync();
        using var service = Service();
        await service.HandleAsync(incident, "tngo", canAct: true, "fix it", default);
        var history = new History();
        var monitor = Monitor(history);

        history.Jobs[9001] = [Job(JobState.Running)];
        await monitor.PassAsync(default);
        Assert.AreEqual(IncidentStatus.Fixing, _store.Rows.Single().Status, "not while the job runs");

        history.Jobs[9001] = [Job(JobState.Failed), Job(JobState.Done, pr: "https://dev.azure.com/o/p/_git/sysmin/pullrequest/77")];
        await monitor.PassAsync(default);
        Assert.AreEqual(IncidentStatus.Resolved, _store.Rows.Single().Status);
        StringAssert.Contains(_chat.SentText[^1], "✅ **Incident #1 is resolved**: agentd's job for Bug #9001 is done (PR https://dev.azure.com/o/p/_git/sysmin/pullrequest/77)");

        _clock.UtcNow = _clock.UtcNow.AddMinutes(5);
        _telemetry.Groups["orders-api"] = [Group(2)];
        Assert.AreEqual(1, await monitor.PassAsync(default));
        Assert.AreEqual(IncidentStatus.Open, _store.Rows.Single().Status);
        Assert.IsTrue(_chat.SentText.Any(t => t.Contains("🔁 **Incident #1 is back**", StringComparison.Ordinal)), "a regression says so in its thread");
    }

    private IncidentService Service()
    {
        _investigator = new IncidentInvestigator(_store, _telemetry, new FakeRegistry(), new FakeWorktrees(), _agent, Registry(), Options.Create(_options),
            NullLogger<IncidentInvestigator>.Instance, _inbox);
        return new IncidentService(_store, _investigator, new FakeRegistry(), Options.Create(_options), Options.Create(new JobOptions()), NullLogger<IncidentService>.Instance,
            _workItems);
    }

    private IncidentMonitor Monitor(History history)
    {
        var messaging = Messaging();
        return new IncidentMonitor(Options.Create(_options), _telemetry, _store, Registry(), Options.Create(messaging), _clock, NullLogger<IncidentMonitor>.Instance,
            _inbox, _investigator, history);
    }

    private MessagingProviderRegistry Registry() => new([_chat], Options.Create(Messaging()));

    private static MessagingOptions Messaging()
    {
        var messaging = new MessagingOptions();
        messaging.Providers["discord"] = new MessagingProviderSettings { Enabled = true };
        return messaging;
    }

    private ErrorGroup Group(long count) => new("ex|p1|GET /orders", ErrorKinds.Exception, "NullReferenceException", "Object reference not set", "GET /orders", null, count, 2,
        _clock.UtcNow.AddMinutes(-10), _clock.UtcNow, new ErrorSample("System.NullReferenceException\n   at Orders.Get()", null, "op-1"));

    private async Task<Incident> IncidentAsync(string? analysis = null, Guid? session = null)
    {
        var recorded = (await _store.RecordAsync("orders-api", Group(5), 1, default))!;
        await _store.SetThreadAsync(recorded.Id, "discord", "thread-9", null, default);
        if (analysis is not null)
        {
            await _store.SetAnalysisAsync(recorded.Id, analysis, session ?? Guid.NewGuid(), default);
        }

        return (await _store.GetAsync(recorded.Id, default))!;
    }

    private Job Job(JobState state, string? pr = null)
    {
        var job = Domain.Jobs.Job.Rehydrate(Domain.Jobs.Job.Create(WorkItemId.From(9001), RepositoryName.From("sysmin"), "t", _clock).ToSnapshot() with
        {
            State = state,
            PullRequest = pr is null ? null : new PullRequestUrl(new Uri(pr)),
        }, _clock);
        return job;
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(done(), "timed out");
    }

    private sealed class History : IWorkItemHistory
    {
        public Dictionary<int, IReadOnlyList<Job>> Jobs { get; } = [];

        public Task<IReadOnlyList<Job>> ListJobsAsync(WorkItemId workItem, CancellationToken cancellationToken) =>
            Task.FromResult(Jobs.GetValueOrDefault(workItem.Value) ?? []);

        public Task<IReadOnlyList<Application.Events.AgentEventDto>> ReadEventsAsync(WorkItemId workItem, long? after, long? before, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<PostedMessage>> ListPostedAsync(WorkItemId workItem, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
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

            return Task.FromResult(new BrainstormReply(Reply, null, null));
        }
    }
}
