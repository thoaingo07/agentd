using System.Net;
using System.Text.Json.Nodes;
using Agentd.Application.Ports;
using Agentd.Infrastructure.AzureDevOps;

namespace Agentd.Infrastructure.Tests.AzureDevOps;

[TestClass]
public sealed class AzureDevOpsSearchTests : IDisposable
{
    private readonly FakeAdo _ado = new();

    [TestMethod]
    public void Search_values_are_quoted_so_they_cant_change_the_query()
    {
        var wiql = AzureDevOpsSearch.Wiql(new WorkItemQuery("x' OR [System.Id] > '0", "Bug", null, "Dev One", null, @"Portal\Platform", "@CurrentIteration"));

        Assert.AreEqual(
            @"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.Title] CONTAINS 'x'' OR [System.Id] > ''0' AND [System.WorkItemType] = 'Bug' AND [System.AssignedTo] CONTAINS 'Dev One' AND [System.AreaPath] UNDER 'Portal\Platform' AND [System.IterationPath] = @CurrentIteration ORDER BY [System.ChangedDate] DESC",
            wiql);
    }

    [TestMethod]
    public async Task Work_items_are_searched_then_read_in_one_batch_in_order()
    {
        _ado.On(HttpMethod.Post, "/ermsystem/Portal/_apis/wit/wiql", HttpStatusCode.OK, """{"workItems":[{"id":5617},{"id":5600}]}""")
            .On(HttpMethod.Post, "/ermsystem/_apis/wit/workitemsbatch", HttpStatusCode.OK, """
                {"value":[
                  {"id":5600,"fields":{"System.WorkItemType":"Bug","System.Title":"Login fails","System.State":"New","System.Tags":"","System.ChangedDate":"2026-10-01T10:00:00Z"}},
                  {"id":5617,"fields":{"System.WorkItemType":"User Story","System.Title":"Deploy to AKS","System.State":"Active","System.AssignedTo":{"displayName":"Dev One"},"System.Tags":"ai-workflow; repo:sysmin","System.ChangedDate":"2026-10-07T10:00:00Z"}}]}
                """);

        var hits = await Search().SearchWorkItemsAsync(new WorkItemQuery(null, null, "Active", null, null, null, null, Top: 500), default);

        CollectionAssert.AreEqual(new[] { 5617, 5600 }, hits.Select(h => h.Id).ToList(), "the WIQL order (newest change first)");
        Assert.AreEqual(("Dev One", "ai-workflow,repo:sysmin"), (hits[0].AssignedTo, string.Join(',', hits[0].Tags)));
        StringAssert.Contains(_ado.Requests[0].Url, "$top=50", "capped");
        StringAssert.Contains(JsonNode.Parse(_ado.Requests[0].Body!)!["query"]!.GetValue<string>(), "[System.State] = 'Active'");
    }

    [TestMethod]
    public async Task Pull_requests_are_listed_for_the_project_or_one_repository()
    {
        _ado.On(HttpMethod.Get, "/ermsystem/Portal/_apis/git/repositories/sysmin/pullrequests", HttpStatusCode.OK, """
            {"value":[{"pullRequestId":3944,"repository":{"name":"sysmin"},"title":"Deploy to AKS","createdBy":{"displayName":"Dev One"},"status":"active",
              "sourceRefName":"refs/heads/ai/5617-deploy","targetRefName":"refs/heads/develop","creationDate":"2026-10-06T09:00:00Z"}]}
            """);

        var prs = await Search().ListPullRequestsAsync("sysmin", "bogus", 10, default);

        Assert.AreEqual(new PullRequestHit(3944, "sysmin", "Deploy to AKS", "Dev One", "active", "ai/5617-deploy", "develop", new DateTimeOffset(2026, 10, 6, 9, 0, 0, TimeSpan.Zero)), prs.Single());
        StringAssert.Contains(_ado.Requests[0].Url, "searchCriteria.status=active", "an unknown status means active");
    }

    public void Dispose() => _ado.Dispose();

    private AzureDevOpsSearch Search() =>
        new(_ado.Client(), Microsoft.Extensions.Options.Options.Create(new AzureDevOpsOptions { Organization = "ermsystem", Project = "Portal" }));
}
