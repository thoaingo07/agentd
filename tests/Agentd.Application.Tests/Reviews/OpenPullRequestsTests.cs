using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using Agentd.Application.Tests.Fakes;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Repositories;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class OpenPullRequestsTests
{
    private readonly FakeRegistry _registry = new();
    private readonly FakePullRequests _prs = new();
    private readonly FakeClock _clock = new();

    [TestMethod]
    public async Task Every_repositorys_active_prs_come_newest_first_and_a_failing_one_is_named()
    {
        _registry.Repositories.Add(new(RepositoryName.From("portal"), "git@erm-azdo:v3/ermsystem/Portal/portal", new AzureDevOpsRepo("ermsystem", "Portal", "portal"), "develop", "repo:portal", []));
        _registry.Repositories.Add(new(RepositoryName.From("docs"), "git@erm-azdo:v3/ermsystem/Portal/docs", new AzureDevOpsRepo("ermsystem", "Portal", "docs"), "main", "repo:docs", []));
        _prs.Active["sysmin"] = [Pr(1, days: 1)];
        _prs.Active["portal"] = [Pr(2, days: 3), Pr(3, days: 0)];
        _prs.ListFailures.Add("docs");

        var list = await Service().ListAsync(default);

        CollectionAssert.AreEqual(new[] { ("portal", 3), ("sysmin", 1), ("portal", 2) }, list.Items.Select(i => (i.Repo, i.PullRequest.Id)).ToList());
        Assert.AreEqual(("docs", "Azure DevOps answered 401."), (list.Failed.Single().Repo, list.Failed.Single().Reason));
        Assert.AreEqual(_clock.UtcNow, list.FetchedAt);
    }

    [TestMethod]
    public async Task Parallel_pages_share_one_read_per_repository_until_it_goes_stale()
    {
        _prs.Active["sysmin"] = [Pr(1, days: 0)];
        var service = Service();

        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.ListAsync(default))));
        _clock.UtcNow += OpenPullRequests.Fresh;
        _prs.Active["sysmin"].Add(Pr(2, days: 0));
        var later = await service.ListAsync(default);

        Assert.IsTrue(all.All(l => l.Items.Count == 1));
        Assert.AreEqual(2, _prs.ListCalls, "one read for twenty pages, then one more once stale");
        Assert.HasCount(2, later.Items);
    }

    [TestMethod]
    public async Task A_failed_read_is_tried_again_next_time()
    {
        _prs.ListFailures.Add("sysmin");
        var service = Service();
        var failed = await service.ListAsync(default);

        _prs.ListFailures.Clear();
        _prs.Active["sysmin"] = [Pr(1, days: 0)];
        var retried = await service.ListAsync(default);

        Assert.HasCount(1, failed.Failed);
        Assert.AreEqual((1, 0), (retried.Items.Count, retried.Failed.Count));
    }

    private OpenPullRequests Service() => new(_registry, _prs, _clock);

    private PullRequestSummary Pr(int id, int days) =>
        new(id, $"PR {id}", "Dev", $"feature/{id}", "develop", false, _clock.UtcNow.AddDays(-days), new Uri($"https://dev.azure.com/x/_git/r/pullrequest/{id}"));
}
