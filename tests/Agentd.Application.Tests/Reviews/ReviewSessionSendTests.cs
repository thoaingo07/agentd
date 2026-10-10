using System.Text;
using Agentd.Application.AzureDevOps;
using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using static Agentd.Application.Tests.AzureDevOps.AdoConnectionsTests;
using static Agentd.Application.Tests.Reviews.ReviewSessionServiceTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewSessionSendTests
{
    private static readonly Guid s_dev = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly TestContext _t = new();
    private readonly MemoryReviewSessions _store = new();
    private readonly MemoryConnections _connections = new();
    private readonly AdoActor _actor = new();

    [TestMethod]
    public async Task A_pr_review_posts_what_was_kept_and_the_comments_under_the_reviewers_name()
    {
        var id = await ReadyAsync(ReviewTargets.PullRequest, 3944);
        _connections.Rows.Add(new(s_dev, "dev@example.com", "Dev One", "dev@example.com", Encoding.UTF8.GetBytes("enc:refresh"), false, null, default, default));
        _t.PullRequests.ActingAs = () => _actor.Current;
        _t.PullRequests.Details[3944] = new PullRequestDetails(3944, "Deploy", null, "Dev", "ai/x", "develop", "h1", PullRequestStatus.Active, false, new Uri("https://dev.azure.com/o/p/_git/sysmin/pullrequest/3944"));

        var sent = (await Send().SendAsync(id, ReviewSessionSend.ToPullRequest, default)).Value!;

        var threads = _t.PullRequests.Threads;
        Assert.AreEqual((4, true, "https://dev.azure.com/o/p/_git/sysmin/pullrequest/3944"), (sent.Posted, sent.AsPerson, sent.Url!.ToString()));
        StringAssert.Contains(threads[0].Text, "Unbounded read");
        StringAssert.Contains(threads[0].Text, "Page it by key, 5000 at a time.", "the edited words");
        Assert.AreEqual(("src/Sync.cs", (int?)41), (threads[0].File, threads[0].Line));
        Assert.IsFalse(threads.Any(t => t.Text.Contains("N+1", StringComparison.Ordinal)), "dropped stays home");
        Assert.AreEqual(("💬 make the size config", "src/Sync.cs", (int?)58), (threads[2].Text, threads[2].File, threads[2].Line));
        StringAssert.Contains(threads[3].Text, "**Review** · 2 finding(s)");
        Assert.DoesNotContain("agentd checks again after every push", threads[3].Text, "a page's post isn't re-checked");
        Assert.IsTrue(_t.PullRequests.ThreadsAs.All(a => a == s_dev), "every thread under the reviewer's name");
        Assert.AreEqual((ReviewSessionStatus.Sent, "pr:3944"), (_store.Sessions[id].Status, _store.Sessions[id].SentTo));
    }

    [TestMethod]
    public async Task Without_a_connection_it_posts_as_agentd_and_a_failure_leaves_it_ready_to_send_again()
    {
        var id = await ReadyAsync(ReviewTargets.PullRequest, 3944);
        _t.PullRequests.FailThread = 2;

        var failed = await Send().SendAsync(id, ReviewSessionSend.ToPullRequest, default);
        _t.PullRequests.FailThread = null;
        var sent = (await Send().SendAsync(id, ReviewSessionSend.ToPullRequest, default)).Value!;

        StringAssert.Contains(failed.Error!.Message, "Posting stopped after 1 thread(s)");
        Assert.IsFalse(sent.AsPerson);
        Assert.AreEqual(ReviewSessionStatus.Sent, _store.Sessions[id].Status);
    }

    [TestMethod]
    public async Task Any_review_can_be_copied_as_text_once()
    {
        var id = await ReadyAsync(ReviewTargets.Branch, null);

        var text = (await Send().SendAsync(id, ReviewSessionSend.AsText, default)).Value!.Text!;
        var toPr = await Send().SendAsync(id, ReviewSessionSend.ToPullRequest, default);

        StringAssert.Contains(text, "1. 🔴 **Unbounded read** · `src/Sync.cs:41`\n   Page it by key, 5000 at a time.");
        StringAssert.Contains(text, "3. 💬 On `src/Sync.cs:58`: make the size config", "the dropped one isn't numbered");
        Assert.AreEqual("conflict", toPr.Error!.Code, "already sent");
    }

    [TestMethod]
    public async Task A_branch_review_has_no_pr_to_post_to()
    {
        var id = await ReadyAsync(ReviewTargets.Branch, null);

        var result = await Send().SendAsync(id, ReviewSessionSend.ToPullRequest, default);

        StringAssert.Contains(result.Error!.Message, "Only a pull request's review");
    }

    private async Task<long> ReadyAsync(string target, int? pr)
    {
        var id = await _store.InsertAsync("sysmin", target, pr, "ai/x", "develop", "dev@example.com", null, null, default);
        await _store.PinAsync(id, "m1", "h1", null, default);
        await _store.AddFindingsAsync(id, [
            new SessionFinding("breaks", "src/Sync.cs", 41, "Unbounded read", "Large tables.", "Page by key."),
            new SessionFinding("performance", "src/Lookup.cs", 9, "N+1 lookups", null, null),
            new SessionFinding("breaks", null, null, "No rollback", "The migration can't be undone.", null),
        ], "One real bug.", default);
        await _store.DecideAsync(id, 0, FindingDecisions.Edited, "Page it by key, 5000 at a time.", default);
        await _store.DecideAsync(id, 1, FindingDecisions.Dropped, null, default);
        await _store.AddCommentAsync(id, "src/Sync.cs", 58, null, "make the size config", "dev@example.com", default);
        await _store.SetStatusAsync(id, ReviewSessionStatus.Ready, null, null, default);
        return id;
    }

    private ReviewSessionSend Send() =>
        new(_store, _t.Registry, _t.PullRequests, new AdoOnBehalf(_connections, new AdoUserTokens(_connections, new FakeProtector(), new FakeDelegation(), _t.Clock), _t.Outbox), _actor);
}
