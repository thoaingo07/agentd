using System.Net;
using System.Text.Json.Nodes;
using Agentd.Infrastructure.AzureDevOps;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Tests.AzureDevOps;

[TestClass]
public sealed class WorkItemSourceTests
{
    private const string Item = """
        {
          "id": 1234, "rev": 7,
          "fields": {
            "System.Title": "Fix login redirect",
            "System.State": "Active",
            "System.AreaPath": "Portal\\Platform",
            "System.Tags": "ai-workflow; repo:sysmin",
            "System.Description": "<div>The login <b>redirect</b> loops.</div><ul><li>Step one</li></ul>",
            "Microsoft.VSTS.Common.AcceptanceCriteria": "<p>Redirects once &amp; lands on /home</p>"
          },
          "_links": { "html": { "href": "https://dev.azure.com/ermsystem/Portal/_workitems/edit/1234" } }
        }
        """;

    private const string Comments = """
        { "comments": [ { "text": "<p>Keep it simple</p>", "createdBy": { "displayName": "tngo" }, "createdDate": "2026-09-28T10:00:00Z" } ] }
        """;

    private static AzureDevOpsWorkItemSource Source(FakeAdo ado) =>
        new(ado.Client(), Options.Create(new AzureDevOpsOptions { Organization = "ermsystem", Project = "Portal" }));

    [TestMethod]
    public void Wiql_escapes_quotes_and_filters_by_tag_claim_tag_and_states()
    {
        var wiql = AzureDevOpsWorkItemSource.BuildWiql("ai-work'flow", "ai-in-progress", ["New", "Active"]);

        StringAssert.Contains(wiql, "[System.Tags] CONTAINS 'ai-work''flow'");
        StringAssert.Contains(wiql, "AND NOT [System.Tags] CONTAINS 'ai-in-progress'");
        StringAssert.Contains(wiql, "[System.State] IN ('New', 'Active')");
        StringAssert.Contains(wiql, "[System.TeamProject] = @project");
    }

    [TestMethod]
    public async Task Query_returns_ids_with_revisions_from_the_batch_api()
    {
        var ado = new FakeAdo()
            .On(HttpMethod.Post, "/ermsystem/Portal/_apis/wit/wiql", HttpStatusCode.OK, """{ "workItems": [ { "id": 1 }, { "id": 2 } ] }""")
            .On(HttpMethod.Post, "/ermsystem/_apis/wit/workitemsbatch", HttpStatusCode.OK, """{ "value": [ { "id": 1, "rev": 3 }, { "id": 2, "rev": 9 } ] }""");

        var refs = await Source(ado).QueryTaggedAsync("ai-workflow", "ai-in-progress", ["Active"], default);

        Assert.HasCount(2, refs);
        Assert.AreEqual(9, refs[1].Rev);
        StringAssert.Contains(JsonNode.Parse(ado.Requests[0].Body!)!["query"]!.GetValue<string>(), "CONTAINS 'ai-workflow'");
    }

    [TestMethod]
    public async Task Get_maps_fields_strips_html_and_reads_comments()
    {
        var ado = new FakeAdo()
            .On(HttpMethod.Get, "/ermsystem/_apis/wit/workitems/1234", HttpStatusCode.OK, Item)
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/wit/workItems/1234/comments", HttpStatusCode.OK, Comments);

        var item = (await Source(ado).GetAsync(1234, default))!;

        Assert.AreEqual(7, item.Rev);
        Assert.AreEqual("Fix login redirect", item.Title);
        CollectionAssert.AreEqual(new List<string> { "ai-workflow", "repo:sysmin" }, item.Tags.ToList());
        StringAssert.Contains(item.Description, "The login redirect loops.");
        StringAssert.Contains(item.Description, "- Step one");
        Assert.AreEqual("Redirects once & lands on /home", item.AcceptanceCriteria);
        Assert.AreEqual("tngo", item.Comments.Single().Author);
        Assert.AreEqual("Keep it simple", item.Comments.Single().Text);
    }

    [TestMethod]
    public async Task Get_unknown_item_returns_null() =>
        Assert.IsNull(await Source(new FakeAdo()).GetAsync(404, default));

    [TestMethod]
    public async Task Claim_patches_with_a_rev_test_and_keeps_existing_tags()
    {
        var ado = new FakeAdo()
            .On(HttpMethod.Get, "/ermsystem/_apis/wit/workitems/1234", HttpStatusCode.OK, Item)
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/wit/workItems/1234/comments", HttpStatusCode.OK, Comments)
            .On(HttpMethod.Patch, "/ermsystem/_apis/wit/workitems/1234", HttpStatusCode.OK, "{}");

        Assert.IsTrue(await Source(ado).TryClaimAsync(1234, 7, "ai-in-progress", default));

        var patch = ado.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.AreEqual("application/json-patch+json", patch.ContentType);
        var ops = JsonNode.Parse(patch.Body!)!.AsArray();
        Assert.AreEqual("test", ops[0]!["op"]!.GetValue<string>());
        Assert.AreEqual(7, ops[0]!["value"]!.GetValue<int>());
        Assert.AreEqual("ai-workflow; repo:sysmin; ai-in-progress", ops[1]!["value"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task Claim_with_a_stale_rev_does_not_patch()
    {
        var ado = new FakeAdo()
            .On(HttpMethod.Get, "/ermsystem/_apis/wit/workitems/1234", HttpStatusCode.OK, Item)
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/wit/workItems/1234/comments", HttpStatusCode.OK, Comments);

        Assert.IsFalse(await Source(ado).TryClaimAsync(1234, 6, "ai-in-progress", default));
        Assert.IsFalse(ado.Requests.Any(r => r.Method == HttpMethod.Patch));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.PreconditionFailed, "")]
    [DataRow(HttpStatusCode.Conflict, "")]
    [DataRow(HttpStatusCode.BadRequest, """{"message":"The test operation failed."}""")]
    public async Task Claim_race_lost_returns_false(HttpStatusCode status, string body)
    {
        var ado = new FakeAdo()
            .On(HttpMethod.Get, "/ermsystem/_apis/wit/workitems/1234", HttpStatusCode.OK, Item)
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/wit/workItems/1234/comments", HttpStatusCode.OK, Comments)
            .On(HttpMethod.Patch, "/ermsystem/_apis/wit/workitems/1234", status, body);

        Assert.IsFalse(await Source(ado).TryClaimAsync(1234, 7, "ai-in-progress", default));
    }

    [TestMethod]
    public async Task Claim_server_error_throws()
    {
        var ado = new FakeAdo()
            .On(HttpMethod.Get, "/ermsystem/_apis/wit/workitems/1234", HttpStatusCode.OK, Item)
            .On(HttpMethod.Get, "/ermsystem/Portal/_apis/wit/workItems/1234/comments", HttpStatusCode.OK, Comments)
            .On(HttpMethod.Patch, "/ermsystem/_apis/wit/workitems/1234", HttpStatusCode.InternalServerError, "{}");

        await Assert.ThrowsExactlyAsync<AdoException>(() => Source(ado).TryClaimAsync(1234, 7, "ai-in-progress", default));
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Redirect)]
    [DataRow(HttpStatusCode.NonAuthoritativeInformation)]
    public async Task A_sign_in_redirect_is_reported_as_an_authentication_error(HttpStatusCode status)
    {
        var ado = new FakeAdo().On(HttpMethod.Post, "/ermsystem/Portal/_apis/wit/wiql", status, "<html>sign in</html>");

        var ex = await Assert.ThrowsExactlyAsync<AdoException>(() => Source(ado).QueryTaggedAsync("t", "c", [], default));

        Assert.AreEqual(401, ex.StatusCode);
        StringAssert.Contains(ex.Message, "rejected the credentials");
    }

    [TestMethod]
    public async Task Comments_are_html_escaped()
    {
        var ado = new FakeAdo().On(HttpMethod.Post, "/ermsystem/Portal/_apis/wit/workItems/1/comments", HttpStatusCode.OK, "{}");

        await Source(ado).AddCommentAsync(1, "agentd <script>alert(1)</script> & done", default);

        var text = JsonNode.Parse(ado.Requests.Single().Body!)!["text"]!.GetValue<string>();
        Assert.AreEqual("agentd &lt;script&gt;alert(1)&lt;/script&gt; &amp; done", text);
    }

    [TestMethod]
    public async Task Creating_a_task_posts_its_fields_estimate_tags_and_parent_link()
    {
        var ado = new FakeAdo().On(HttpMethod.Post, "/ermsystem/Portal/_apis/wit/workitems/$Task", HttpStatusCode.OK,
            """{ "id": 9002, "_links": { "html": { "href": "https://dev.azure.com/ermsystem/Portal/_workitems/edit/9002" } } }""");

        var created = await Source(ado).CreateAsync(new Agentd.Application.Ports.NewWorkItem(
            "Task", "Theme tokens", "Add tokens:\n- dark\n- light", "<b>works</b>", ["ui", "ai-workflow"], 4, 9001, "Portal\\Platform"), default);

        Assert.AreEqual((9002, "https://dev.azure.com/ermsystem/Portal/_workitems/edit/9002"), (created.Id, created.Url!.ToString()));
        var request = ado.Requests.Single();
        Assert.AreEqual("application/json-patch+json", request.ContentType);
        var ops = JsonNode.Parse(request.Body!)!.AsArray().ToDictionary(o => o!["path"]!.GetValue<string>(), o => o!["value"]!);
        Assert.AreEqual("Theme tokens", ops["/fields/System.Title"].GetValue<string>());
        Assert.AreEqual("<p>Add tokens:</p><ul><li>dark</li><li>light</li></ul>", ops["/fields/System.Description"].GetValue<string>());
        Assert.AreEqual("<p>&lt;b&gt;works&lt;/b&gt;</p>", ops["/fields/Microsoft.VSTS.Common.AcceptanceCriteria"].GetValue<string>(), "escaped, never raw HTML");
        Assert.AreEqual("ui; ai-workflow", ops["/fields/System.Tags"].GetValue<string>());
        Assert.AreEqual(4, ops["/fields/Microsoft.VSTS.Scheduling.OriginalEstimate"].GetValue<double>());
        Assert.AreEqual("Portal\\Platform", ops["/fields/System.AreaPath"].GetValue<string>());
        Assert.AreEqual("System.LinkTypes.Hierarchy-Reverse", ops["/relations/-"]["rel"]!.GetValue<string>());
        StringAssert.EndsWith(ops["/relations/-"]["url"]!.GetValue<string>(), "ermsystem/_apis/wit/workItems/9001");
    }
}
