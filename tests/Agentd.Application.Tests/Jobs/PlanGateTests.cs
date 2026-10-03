using Agentd.Application.Jobs;
using Agentd.Application.Messaging;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class PlanGateTests
{
    private readonly TestContext _t = new();

    public PlanGateTests() => _t.Options.Value.RequirePlanApproval = true;

    [TestMethod]
    public async Task The_first_turn_is_read_only_until_the_plan_is_approved()
    {
        var request = await _t.RunningJobAsync();

        Assert.IsTrue(request.ReadOnly);
        Assert.AreEqual(PlanStatus.Pending, _t.Jobs.Get(request.JobId).PlanStatus);
        StringAssert.Contains(request.Prompt, "END YOUR TURN: you can't edit files until the developer approves");
        Assert.AreEqual("plan_not_approved", (await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default)).Error?.Code);
    }

    [TestMethod]
    public async Task An_ai_auto_work_item_skips_the_gate()
    {
        _t.WorkItems.Add(77, tags: ["ai-workflow", "ai-auto"]);
        await _t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(77)), default);

        var request = (await _t.StartNext().Handle(new StartNextJob("w1"), default)).Value!;

        Assert.IsFalse(request.ReadOnly);
        Assert.AreEqual(PlanStatus.NotRequired, _t.Jobs.Get(request.JobId).PlanStatus);
        Assert.AreEqual(PlanOutcome.Continue, (await Submit(request.JobId)).Value);
        StringAssert.StartsWith(_t.Outbox.Enqueued[^1].Message.Message.Markdown, "📝 **Plan**");
    }

    [TestMethod]
    public async Task Submitting_asks_for_approval_with_the_estimate_and_waits()
    {
        var request = await _t.RunningJobAsync();
        _t.Activity.RecordUsage(request.JobId, new UsageSnapshot(0.62, 0.3, null));

        Assert.AreEqual(PlanOutcome.AwaitingApproval, (await Submit(request.JobId)).Value);

        var job = _t.Jobs.Get(request.JobId);
        Assert.AreEqual(JobState.WaitingForHuman, job.State);
        Assert.AreEqual(new PlanEstimate(25, 15, 0.62, _t.Clock.UtcNow), job.Estimate);
        var question = (DeveloperQuestionAsked)_t.Jobs.SavedEvents.Single(e => e is DeveloperQuestionAsked);
        StringAssert.Contains(question.Question, "**Estimate:** ~25 min, ~15% of the 5-hour usage window (now at 62%).");
        CollectionAssert.AreEqual(new[] { SubmitPlanHandler.ApproveLabel, SubmitPlanHandler.ChangesLabel }, question.Options.ToArray());
    }

    [TestMethod]
    public async Task A_change_request_keeps_it_read_only_and_an_approval_unlocks_editing()
    {
        var request = await _t.RunningJobAsync();
        await Submit(request.JobId);
        var submit = new SubmitDeveloperMessageHandler(_t.Jobs);
        var resume = new ResumeJobTurnHandler(_t.Jobs, _t.Outbox);

        await submit.Handle(new SubmitDeveloperMessage(request.JobId, "drop step 3", "tngo"), default);
        var revise = (await resume.Handle(new ResumeJobTurn([]), default)).Value!;
        Assert.IsTrue(revise.ReadOnly);
        StringAssert.Contains(revise.Prompt, "revise it with this feedback and call `submit_plan` again");
        Assert.AreEqual(PlanStatus.Pending, _t.Jobs.Get(request.JobId).PlanStatus);

        await Submit(request.JobId);
        await submit.Handle(new SubmitDeveloperMessage(request.JobId, SubmitPlanHandler.ApproveLabel, "tngo"), default);
        var implement = (await resume.Handle(new ResumeJobTurn([]), default)).Value!;

        Assert.IsFalse(implement.ReadOnly);
        var job = _t.Jobs.Get(request.JobId);
        Assert.AreEqual(PlanStatus.Approved, job.PlanStatus);
        Assert.AreEqual(_t.Clock.UtcNow, job.Estimate!.ApprovedAt);
        StringAssert.Contains(JobEventMessages.For(_t.Jobs.SavedEvents.OfType<PlanApproved>().Single())!.Message.Markdown, "**Plan approved** by tngo");
    }

    [TestMethod]
    [DataRow("1", true)]
    [DataRow("approve", true)]
    [DataRow("Approved!", true)]
    [DataRow("approve, but keep the routes", true)]
    [DataRow("LGTM", true)]
    [DataRow("👍", true)]
    [DataRow("2", false)]
    [DataRow("✏️ Request changes", false)]
    [DataRow("ok but change step 2", false)]
    [DataRow("why this approach?", false)]
    public void Approval_replies_are_recognized(string reply, bool approves) =>
        Assert.AreEqual(approves, SubmitPlanHandler.IsApproval(reply));

    [TestMethod]
    public async Task The_pull_request_reports_actual_against_the_estimate()
    {
        var request = await _t.RunningJobAsync();
        _t.Activity.RecordUsage(request.JobId, new UsageSnapshot(0.40, null, null));
        await Submit(request.JobId);
        await new SubmitDeveloperMessageHandler(_t.Jobs).Handle(new SubmitDeveloperMessage(request.JobId, "lgtm", "tngo"), default);
        await new ResumeJobTurnHandler(_t.Jobs, _t.Outbox).Handle(new ResumeJobTurn([]), default);
        _t.Clock.UtcNow = _t.Clock.UtcNow.AddMinutes(34);
        _t.Activity.RecordUsage(request.JobId, new UsageSnapshot(0.58, null, null));

        Assert.IsTrue((await _t.Finish().Handle(new FinishWork(request.JobId, "T", "D", "S"), default)).IsSuccess);

        Assert.AreEqual("📊 **Actual:** 34 min vs ~25 min estimated; usage +18% of the 5-hour window (est. 15%).",
            _t.Outbox.Enqueued.Select(e => e.Message.Message.Markdown).Single(m => m.StartsWith("📊", StringComparison.Ordinal)));
    }

    private Task<Domain.Common.Result<PlanOutcome>> Submit(JobId job) =>
        new SubmitPlanHandler(_t.Jobs, _t.Outbox, _t.Activity, _t.Clock).Handle(new SubmitPlan(job, "1. Edit AGENTS.md\n2. Verify the commands", 25, 15), default);
}
