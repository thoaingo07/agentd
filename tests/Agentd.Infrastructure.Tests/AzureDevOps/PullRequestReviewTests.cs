using System.Net;
using System.Text.Json.Nodes;
using Agentd.Application.Ports;
using Agentd.Domain.Repositories;
using Agentd.Infrastructure.AzureDevOps;

namespace Agentd.Infrastructure.Tests.AzureDevOps;

[TestClass]
public sealed class PullRequestReviewTests
{
    private static readonly Repository s_repo = Repository.From(RemoteUrl.Parse("git@erm-azdo:v3/ermsystem/Portal/sysmin").Value!, null, "develop", "repo:sysmin", []);
    private const string PrPath = "/ermsystem/Portal/_apis/git/repositories/sysmin/pullrequests/3935";

    [TestMethod]
    [DataRow("active", PullRequestStatus.Active)]
    [DataRow("completed", PullRequestStatus.Completed)]
    [DataRow("abandoned", PullRequestStatus.Abandoned)]
    public async Task Status_maps_the_pr_state(string status, PullRequestStatus expected)
    {
        var ado = new FakeAdo().On(HttpMethod.Get, PrPath, HttpStatusCode.OK, $$"""{ "pullRequestId": 3935, "status": "{{status}}" }""");

        Assert.AreEqual(expected, await new AzureDevOpsPullRequests(ado.Client()).GetStatusAsync(s_repo, 3935, default));
    }

    [TestMethod]
    public async Task Comments_keep_human_text_and_skip_system_deleted_and_agentds_own()
    {
        var ado = new FakeAdo().On(HttpMethod.Get, PrPath + "/threads", HttpStatusCode.OK, """
            { "value": [
              { "id": 10, "status": "active", "threadContext": { "filePath": "/AGENTS.md", "rightFileStart": { "line": 42 } },
                "comments": [
                  { "id": 1, "commentType": "text", "content": "Please mention the 02:00 CronJob.", "author": { "displayName": "Reviewer" }, "publishedDate": "2026-10-03T18:00:00Z" },
                  { "id": 2, "commentType": "text", "content": "🤖 agentd: Addressed in 7294aa4.", "author": { "displayName": "Thoai Ngo" }, "publishedDate": "2026-10-03T18:05:00Z" } ] },
              { "id": 11, "status": "fixed",
                "comments": [ { "id": 3, "commentType": "text", "content": "Typo in the title", "author": { "displayName": "Reviewer" }, "publishedDate": "2026-10-03T17:00:00Z" } ] },
              { "id": 12, "status": "active",
                "comments": [ { "id": 4, "commentType": "system", "content": "Policy status updated", "author": { "displayName": "System" } } ] },
              { "id": 13, "status": "active", "isDeleted": true,
                "comments": [ { "id": 5, "commentType": "text", "content": "gone", "author": { "displayName": "Reviewer" } } ] }
            ] }
            """);

        var comments = await new AzureDevOpsPullRequests(ado.Client()).ListCommentsAsync(s_repo, 3935, default);

        Assert.HasCount(2, comments);
        Assert.AreEqual((11, "Typo in the title", false), (comments[0].ThreadId, comments[0].Content, comments[0].IsOpen), "oldest first; a fixed thread is closed");
        var open = comments[1];
        Assert.AreEqual((10, 1, "Reviewer", "/AGENTS.md", (int?)42, true), (open.ThreadId, open.CommentId, open.Author, open.FilePath, open.Line, open.IsOpen));
    }

    [TestMethod]
    public async Task Replies_go_to_the_thread_with_the_agentd_marker()
    {
        var ado = new FakeAdo().On(HttpMethod.Post, PrPath + "/threads/10/comments", HttpStatusCode.OK, """{ "id": 3 }""");

        await new AzureDevOpsPullRequests(ado.Client()).ReplyAsync(s_repo, 3935, 10, "Addressed in 1a2b3c4.", default);

        var body = JsonNode.Parse(ado.Requests.Single().Body!)!;
        Assert.AreEqual("🤖 agentd: Addressed in 1a2b3c4.", body["content"]!.GetValue<string>());
        Assert.AreEqual(1, body["parentCommentId"]!.GetValue<int>());
    }
}
