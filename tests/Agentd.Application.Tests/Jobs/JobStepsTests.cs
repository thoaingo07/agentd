using Agentd.Application.Jobs;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class JobStepsTests
{
    private static readonly WorktreePath s_worktree = new("/tmp/wt/wi-1234");
    private static readonly BranchName s_branch = BranchName.For(WorkItemId.From(1234), "Fix login");

    [TestMethod]
    public void Each_turn_is_named_after_its_step_of_the_cycle()
    {
        var job = Job.Create(WorkItemId.From(1234), RepositoryName.From("sysmin"), "Fix login", new Fakes.FakeClock());
        job.BeginPreparing();
        job.Start(s_worktree, s_branch, ClaudeSessionId.New(), requirePlanApproval: true);
        Assert.AreEqual(JobSteps.Plan, JobSteps.Of(job), "read-only until the plan is approved");

        job.SubmitPlan(new PlanEstimate(25, 15, null, DateTimeOffset.UtcNow));
        job.ApprovePlan("tngo");
        Assert.AreEqual(JobSteps.Implement, JobSteps.Of(job));

        job.Finish(Domain.Jobs.ValueObjects.PullRequestDraft.Create("Fix login", "desc", "sum").Value!);
        job.OpenForReview(new PullRequestUrl(new Uri("https://dev.azure.com/org/proj/_git/repo/pullrequest/1")));
        Assert.AreEqual(JobSteps.Fix, JobSteps.Of(job), "with a PR, a turn is a fix round");
    }

    [TestMethod]
    public void A_step_takes_its_model_and_effort_from_the_options()
    {
        var options = new JobOptions();
        options.Steps["Plan"] = new StepModel { Model = " opus ", Effort = "high" };
        options.Steps["implement"] = new StepModel { Model = "sonnet" };

        var plan = JobSteps.Apply(Request(JobSteps.Plan), options);
        var implement = JobSteps.Apply(Request(JobSteps.Implement), options);
        var fix = JobSteps.Apply(Request(JobSteps.Fix), options);
        var own = JobSteps.Apply(Request(JobSteps.Plan) with { Model = "haiku" }, options);

        Assert.AreEqual(("opus", "high"), (plan.Model, plan.Effort), "step names ignore case; values are trimmed");
        Assert.AreEqual(("sonnet", null), (implement.Model, implement.Effort));
        Assert.AreEqual((null, null), (fix.Model, fix.Effort), "no entry: the runner's default");
        Assert.AreEqual("haiku", own.Model, "a turn's own model wins");
        Assert.IsNull(JobSteps.Apply(Request(null), options).Model, "no step, nothing applied");
    }

    private static readonly AgentRunRequest s_unstepped = new(new JobId(1), WorkItemId.From(1234), s_worktree, ClaudeSessionId.New(), "go", Resume: false);

    private static AgentRunRequest Request(string? step) => step is null ? s_unstepped : s_unstepped with { Step = step };
}
