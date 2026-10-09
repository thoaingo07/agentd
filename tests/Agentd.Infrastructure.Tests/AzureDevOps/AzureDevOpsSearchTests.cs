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

    [TestMethod]
    public async Task Runs_of_a_pipeline_by_name_are_listed_with_branch_and_result_filters()
    {
        _ado.On(HttpMethod.Get, "/ermsystem/Portal/_apis/build/definitions", HttpStatusCode.OK, """{"value":[{"id":12,"name":"sysmin-ci","path":"\\ci"}]}""")
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/build/builds", HttpStatusCode.OK, """
                {"value":[{"id":901,"definition":{"name":"sysmin-ci"},"buildNumber":"20261008.3","status":"completed","result":"failed","sourceBranch":"refs/heads/develop",
                  "requestedFor":{"displayName":"Dev One"},"reason":"individualCI","sourceVersion":"a1b2c3d4e5","startTime":"2026-10-08T09:00:00Z","finishTime":"2026-10-08T09:06:00Z"}]}
                """);

        var runs = await Search().ListBuildsAsync("sysmin", "develop", "Failed", 500, default);

        Assert.AreEqual(("sysmin-ci", "failed", "develop", "Dev One"), (runs.Single().Pipeline, runs[0].Result, runs[0].Branch, runs[0].RequestedFor));
        StringAssert.Contains(_ado.Requests[0].Url, "name=%2Asysmin%2A".Replace("%2A", "*", StringComparison.Ordinal));
        var url = _ado.Requests[1].Url;
        foreach (var part in new[] { "definitions=12", "branchName=refs%2Fheads%2Fdevelop", "resultFilter=failed", "$top=50" })
        {
            StringAssert.Contains(url, part);
        }
    }

    [TestMethod]
    public async Task An_unknown_pipeline_has_no_runs_instead_of_every_run()
    {
        _ado.On(HttpMethod.Get, "/ermsystem/Portal/_apis/build/definitions", HttpStatusCode.OK, """{"value":[]}""");

        var runs = await Search().ListBuildsAsync("nope", null, null, 10, default);

        Assert.IsEmpty(runs);
        Assert.HasCount(1, _ado.Requests, "no build query without a pipeline");
    }

    [TestMethod]
    public async Task A_failed_run_has_its_failed_steps_errors_and_the_end_of_their_logs()
    {
        var log = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"2026-10-08T09:05:{i % 60:00}.1234567Z line {i}"));
        _ado.On(HttpMethod.Get, "/ermsystem/Portal/_apis/build/builds/901", HttpStatusCode.OK, """
                {"id":901,"definition":{"name":"sysmin-ci"},"buildNumber":"20261008.3","status":"completed","result":"failed","sourceBranch":"refs/heads/develop","reason":"manual"}
                """)
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/build/builds/901/timeline", HttpStatusCode.OK, """
                {"records":[
                  {"name":"Build","type":"Job","result":"failed","issues":[]},
                  {"name":"restore","type":"Task","result":"succeeded"},
                  {"name":"dotnet test","type":"Task","result":"failed","log":{"id":7},"issues":[{"type":"error","message":"Process completed with exit code 1."},{"type":"warning","message":"slow"}]}]}
                """)
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/build/builds/901/logs/7", HttpStatusCode.OK, log);

        var detail = (await Search().GetBuildAsync(901, default))!;

        Assert.AreEqual(("dotnet test", "Build"), (detail.Failures[0].Step, detail.Failures[1].Step), "the step first, then its job");
        CollectionAssert.AreEqual(new[] { "Process completed with exit code 1." }, detail.Failures[0].Issues.ToList(), "errors only");
        var tail = detail.Failures[0].LogTail!.Split('\n');
        Assert.AreEqual((AzureDevOpsSearch.LogTailLines, "line 61", "line 100"), (tail.Length, tail[0], tail[^1]), "the last lines, without timestamps");
        Assert.IsNull(detail.Failures[1].LogTail, "a job's log is all its steps; only steps get one");
        Assert.IsNull(await Search().GetBuildAsync(5, default));
    }

    [TestMethod]
    public void Wiki_search_goes_to_the_search_host_on_azure_devops_services_only()
    {
        Assert.AreEqual(new Uri("https://almsearch.dev.azure.com/"), AzureDevOpsSearch.SearchHost(new Uri("https://dev.azure.com/")));
        Assert.AreEqual(new Uri("https://tfs.example.com/tfs/"), AzureDevOpsSearch.SearchHost(new Uri("https://tfs.example.com/tfs/")));
    }

    [TestMethod]
    public async Task Wiki_search_sends_the_text_as_a_value_and_returns_plain_snippets()
    {
        _ado.On(HttpMethod.Post, "/ermsystem/Portal/_apis/search/wikisearchresults", HttpStatusCode.OK, """
            {"count":1,"results":[{"fileName":"Deploy.md","path":"/Runbooks/Deploy","wiki":{"name":"Portal.wiki"},
              "hits":[{"field":"content","highlights":["run the <highlighthit>helm</highlighthit> upgrade\nwith --atomic"]}]}]}
            """);

        var hits = await Search().SearchWikiAsync("helm \" OR 1=1", 500, default);

        Assert.AreEqual(("Portal.wiki", "/Runbooks/Deploy", "run the helm upgrade with --atomic"), (hits.Single().Wiki, hits[0].Path, hits[0].Snippets.Single()));
        var body = JsonNode.Parse(_ado.Requests[0].Body!)!;
        Assert.AreEqual(("helm \" OR 1=1", 50, "Portal"), (body["searchText"]!.GetValue<string>(), body["$top"]!.GetValue<int>(), body["filters"]!["Project"]![0]!.GetValue<string>()));
    }

    [TestMethod]
    public async Task A_wiki_page_comes_from_the_project_wiki_unless_another_is_named()
    {
        _ado.On(HttpMethod.Get, "/ermsystem/Portal/_apis/wiki/wikis", HttpStatusCode.OK, """
                {"value":[{"id":"w-code","name":"sysmin docs","type":"codeWiki"},{"id":"w-proj","name":"Portal.wiki","type":"projectWiki"}]}
                """)
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/wiki/wikis/w-proj/pages", HttpStatusCode.OK, """
                {"path":"/Runbooks/Deploy","content":"# Deploy","subPages":[{"path":"/Runbooks/Deploy/Rollback"}]}
                """)
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/wiki/wikis/w-code/pages", HttpStatusCode.NotFound);

        var page = (await Search().GetWikiPageAsync(null, "Runbooks/Deploy", default))!;
        var other = await Search().GetWikiPageAsync("SYSMIN DOCS", null, default);

        Assert.AreEqual(("Portal.wiki", "/Runbooks/Deploy", "# Deploy"), (page.Wiki, page.Path, page.Content));
        CollectionAssert.AreEqual(new[] { "/Runbooks/Deploy/Rollback" }, page.SubPages.ToList());
        CollectionAssert.AreEqual(new[] { "sysmin docs", "Portal.wiki" }, page.Wikis.ToList());
        StringAssert.Contains(_ado.Requests[1].Url, "path=%2FRunbooks%2FDeploy");
        Assert.IsNull(other, "no such page in the code wiki");
        StringAssert.Contains(_ado.Requests[3].Url, "/wikis/w-code/pages?path=%2F&");
        Assert.IsNull(await Search().GetWikiPageAsync("nope", null, default));
    }

    public void Dispose() => _ado.Dispose();

    private AzureDevOpsSearch Search() =>
        new(_ado.Client(), Microsoft.Extensions.Options.Options.Create(new AzureDevOpsOptions { Organization = "ermsystem", Project = "Portal" }));
}
