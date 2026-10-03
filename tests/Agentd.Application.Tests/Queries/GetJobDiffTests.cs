using Agentd.Application.Ports;
using Agentd.Application.Queries;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Jobs.ValueObjects;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Queries;

[TestClass]
public sealed class GetJobDiffTests
{
    [TestMethod]
    public async Task Diffs_the_jobs_branch_in_its_worktree_with_the_2_MB_cap()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        var worktrees = new FakeWorktrees { Diff = new BranchDiff("origin/develop", "ai/1234-x", ["a.cs"], "diff", false) };
        var job = t.Jobs.Get(request.JobId);

        var result = await new GetJobDiffHandler(t.Jobs, t.Registry, worktrees).Handle(new GetJobDiff(request.JobId), default);

        Assert.AreEqual(worktrees.Diff, result.Value);
        Assert.AreEqual((job.Branch!.Value.Value, job.Worktree!.Value.Value, 2 * 1024 * 1024), worktrees.Diffed.Single());
    }

    [TestMethod]
    public async Task A_missing_job_or_branch_is_not_found()
    {
        var t = new TestContext();
        var request = await t.RunningJobAsync();
        var handler = new GetJobDiffHandler(t.Jobs, t.Registry, new FakeWorktrees { Diff = null });

        Assert.AreEqual("not_found", (await handler.Handle(new GetJobDiff(new JobId(999)), default)).Error!.Code);
        Assert.AreEqual("not_found", (await handler.Handle(new GetJobDiff(request.JobId), default)).Error!.Code);
    }
}
