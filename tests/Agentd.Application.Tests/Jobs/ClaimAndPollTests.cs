using Agentd.Application.Jobs;
using Agentd.Domain.Jobs;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class ClaimAndPollTests
{
    [TestMethod]
    public async Task Claim_queues_a_job_tags_and_comments_the_work_item()
    {
        var t = new TestContext();
        t.WorkItems.Add(1234);

        var result = await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(1234)), CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.ToString());
        var job = t.Jobs.Single();
        Assert.AreEqual(JobState.Queued, job.State);
        Assert.AreEqual("sysmin", job.Repository.Value);
        Assert.Contains(1234, t.WorkItems.Claimed);
        StringAssert.Contains(t.WorkItems.Comments.Single().Text, "agentd picked this up");
    }

    [TestMethod]
    public async Task Claim_refuses_a_second_active_job_for_the_same_work_item()
    {
        var t = new TestContext();
        t.WorkItems.Add(1234);
        await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(1234)), CancellationToken.None);

        var second = await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(1234), Force: true), CancellationToken.None);

        Assert.AreEqual("conflict", second.Error?.Code);
    }

    [TestMethod]
    public async Task Losing_the_claim_race_creates_no_job()
    {
        var t = new TestContext();
        t.WorkItems.Add(1234);
        t.WorkItems.LoseClaimRace = true;

        var result = await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(1234)), CancellationToken.None);

        Assert.AreEqual("conflict", result.Error?.Code);
        Assert.IsEmpty(await t.Jobs.ListByStateAsync([JobState.Queued], CancellationToken.None));
    }

    [TestMethod]
    public async Task No_repository_match_comments_once_and_creates_no_job()
    {
        var t = new TestContext();
        t.WorkItems.Add(99, areaPath: "Other\\Team");

        var first = await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(99)), CancellationToken.None);
        await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(99)), CancellationToken.None);

        Assert.AreEqual("not_found", first.Error?.Code);
        Assert.HasCount(1, t.WorkItems.Comments);
        StringAssert.Contains(t.WorkItems.Comments[0].Text, "no registered repository");
        Assert.IsEmpty(t.WorkItems.Claimed);
    }

    [TestMethod]
    public async Task Ambiguous_match_is_refused_with_a_comment()
    {
        var t = new TestContext();
        t.Registry.Repositories.Add(new Repository(RepositoryName.From("other"), "git@x:v3/o/p/other", new AzureDevOpsRepo("o", "p", "other"), "main", null, ["Portal\\Platform"]));
        t.WorkItems.Add(5);

        var result = await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(5)), CancellationToken.None);

        Assert.AreEqual("validation", result.Error?.Code);
        StringAssert.Contains(t.WorkItems.Comments.Single().Text, "several repositories");
    }

    [TestMethod]
    public async Task Unknown_work_item_is_not_found()
    {
        var t = new TestContext();

        var result = await t.Claim().Handle(new ClaimWorkItem(WorkItemId.From(404)), CancellationToken.None);

        Assert.AreEqual("not_found", result.Error?.Code);
    }

    [TestMethod]
    public async Task Poll_claims_every_tagged_unclaimed_item()
    {
        var t = new TestContext();
        t.WorkItems.Add(1);
        t.WorkItems.Add(2);
        t.WorkItems.Add(3, areaPath: "Nowhere");   // no repo match → not claimed

        var claimed = await new PollWorkItemsHandler(t.WorkItems, t.Claim(), t.Options).Handle(new PollWorkItems(), CancellationToken.None);

        Assert.AreEqual(2, claimed.Value);
    }
}
