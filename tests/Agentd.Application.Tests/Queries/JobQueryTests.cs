using System.Text.Json;
using Agentd.Application.Events;
using Agentd.Application.Ports;
using Agentd.Application.Queries;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Queries;

[TestClass]
public sealed class JobQueryTests
{
    [TestMethod]
    public async Task The_dashboard_lists_active_jobs_waiting_ones_first()
    {
        var t = new TestContext();
        var running = await t.RunningJobAsync(1);
        var waiting = await t.RunningJobAsync(2);
        var job = t.Jobs.Get(waiting.JobId);
        job.AskDeveloper("v1 or v2?");
        await t.Jobs.SaveAsync(job, default);
        t.Activity.SetPhase(running.JobId, "implement");

        var dashboard = await new GetDashboardHandler(t.Jobs, new Reader([41, 42]), t.Activity, t.Clock).Handle(new GetDashboard(), default);

        CollectionAssert.AreEqual(new[] { waiting.JobId.Value, running.JobId.Value }, dashboard.ActiveJobs.Select(j => j.Id).ToArray());
        Assert.AreEqual("implement", dashboard.ActiveJobs[1].Phase);
        Assert.AreEqual(1, dashboard.CountsByState[JobState.WaitingForHuman]);
        Assert.AreEqual(42, dashboard.LatestSeq, "the stream position the snapshot is at");
    }

    [TestMethod]
    public async Task A_job_detail_carries_its_threads_and_live_activity()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        t.Activity.RecordActivity(request.JobId, "🔧 dotnet test", t.Clock.UtcNow);

        var detail = await new GetJobHandler(t.Jobs, t.Conversations, t.Activity, t.Clock).Handle(new GetJob(request.JobId), default);

        Assert.AreEqual(1234, detail!.Summary.WorkItemId);
        Assert.AreEqual("🔧 dotnet test", detail.LastActivity);
        Assert.IsNull(await new GetJobHandler(t.Jobs, t.Conversations, t.Activity, t.Clock).Handle(new GetJob(new JobId(999)), default));
    }

    [TestMethod]
    public async Task Open_permission_requests_show_on_the_dashboard_and_the_job()
    {
        var t = new TestContext();
        var asking = await t.RunningJobAsync(1);
        var quiet = await t.RunningJobAsync(2);
        var store = new Permissions.PermissionTests.FakeStore();
        await store.InsertAsync(asking.JobId, "Bash", "npm install", ["Bash(npm install:*)"], default);
        await store.DecideAsync(await store.InsertAsync(asking.JobId, "Bash", "make", ["Bash(make:*)"], default), "denied", null, "tngo", Domain.Jobs.ValueObjects.RepositoryName.From("sysmin"), default);

        var dashboard = await new GetDashboardHandler(t.Jobs, new Reader([1]), t.Activity, t.Clock, store).Handle(new GetDashboard(), default);
        var detail = await new GetJobHandler(t.Jobs, t.Conversations, t.Activity, t.Clock, store).Handle(new GetJob(asking.JobId), default);

        Assert.AreEqual((1, 0), (dashboard.ActiveJobs.Single(j => j.Id == asking.JobId.Value).PendingPermissions, dashboard.ActiveJobs.Single(j => j.Id == quiet.JobId.Value).PendingPermissions));
        Assert.AreEqual("npm install", detail!.Permissions!.Single().Summary, "only the open one");
        Assert.AreEqual(1, detail.Summary.PendingPermissions);
    }

    [TestMethod]
    public async Task Revoking_a_rule_that_is_gone_is_not_found()
    {
        var store = new Permissions.PermissionTests.FakeStore();
        store.Rules.Add(new Application.Permissions.PermissionRule(7, "sysmin", null, "Bash(npm install:*)", "tngo", DateTimeOffset.UtcNow));
        var revoke = new Application.Permissions.RevokePermissionRuleHandler(store);

        Assert.IsTrue((await revoke.Handle(new Application.Permissions.RevokePermissionRule(7, "admin"), default)).IsSuccess);
        Assert.AreEqual("not_found", (await revoke.Handle(new Application.Permissions.RevokePermissionRule(7, "admin"), default)).Error!.Code);
        Assert.IsEmpty(await new Application.Permissions.GetPermissionRulesHandler(store).Handle(new Application.Permissions.GetPermissionRules(), default));
    }

    [TestMethod]
    public async Task Event_pages_report_whether_more_exist_in_that_direction()
    {
        var reader = new Reader(Enumerable.Range(1, 10).Select(i => (long)i).ToList());
        var handler = new GetJobEventsHandler(reader);

        var first = await handler.Handle(new GetJobEvents(new JobId(1), null, null, 4), default);
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, first.Events.Select(e => e.Seq).ToArray());
        Assert.IsTrue(first.HasMore);

        var earlier = await handler.Handle(new GetJobEvents(new JobId(1), null, 9, 3), default);
        CollectionAssert.AreEqual(new long[] { 6, 7, 8 }, earlier.Events.Select(e => e.Seq).ToArray());
        Assert.AreEqual((6L, 8L, true), (earlier.OldestSeq!.Value, earlier.NewestSeq!.Value, earlier.HasMore));

        var last = await handler.Handle(new GetJobEvents(new JobId(1), 8, null, 5), default);
        CollectionAssert.AreEqual(new long[] { 9, 10 }, last.Events.Select(e => e.Seq).ToArray());
        Assert.IsFalse(last.HasMore);
    }

    [TestMethod]
    [DataRow("WI-5613", null, 5613)]
    [DataRow("wi5613", null, 5613)]
    [DataRow("5613", "5613", 5613)]
    [DataRow("  AGENTS.md ", "AGENTS.md", null)]
    [DataRow("WI-", "WI-", null)]
    [DataRow("", null, null)]
    public void History_text_means_a_title_or_an_exact_work_item(string text, string? title, int? workItem)
    {
        var (t, w) = SearchHistoryHandler.ParseText(text);

        Assert.AreEqual(title, t);
        Assert.AreEqual(workItem, w?.Value);
    }

    private sealed class Reader(List<long> seqs) : IEventReader
    {
        public Task<IReadOnlyList<AgentEventDto>> ReadAfterAsync(JobId? jobId, long afterSeq, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentEventDto>>(seqs.Where(s => s > afterSeq).Take(limit).Select(Event).ToList());

        public Task<IReadOnlyList<AgentEventDto>> ReadBeforeAsync(JobId jobId, long beforeSeq, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AgentEventDto>>(seqs.Where(s => s < beforeSeq).TakeLast(limit).Select(Event).ToList());

        public Task<AgentEventDto?> GetAsync(long seq, CancellationToken cancellationToken) => Task.FromResult<AgentEventDto?>(null);

        public Task<long> LatestSeqAsync(CancellationToken cancellationToken) => Task.FromResult(seqs.Count == 0 ? 0 : seqs.Max());

        private static AgentEventDto Event(long seq) => new(seq, 1, DateTimeOffset.UnixEpoch, "x", JsonDocument.Parse("{}").RootElement.Clone());
    }
}
