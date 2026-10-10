using System.Text;
using Agentd.Application.AzureDevOps;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using static Agentd.Application.Tests.AzureDevOps.AdoConnectionsTests;
using static Agentd.Application.Tests.Reviews.ReviewSessionServiceTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewSessionFixerTests
{
    private static readonly Guid s_dev = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly TestContext _t = new();
    private readonly MemoryReviewSessions _store = new();
    private readonly MemoryConnections _connections = new();
    private readonly AdoActor _actor = new();
    private readonly Agent _agent = new();

    [TestMethod]
    public async Task Fix_it_fixes_on_the_pr_branch_pushes_there_and_reviews_the_new_head()
    {
        var id = await ReadyPrReviewAsync();
        _connections.Rows.Add(new(s_dev, "dev@example.com", "Dev One", "dev@example.com", Encoding.UTF8.GetBytes("enc:refresh"), false, null, default, default));
        _t.PullRequests.ActingAs = () => _actor.Current;
        var (send, fixer) = Wire();

        var sent = (await send.SendAsync(id, ReviewSessionSend.FixIt, default)).Value!;
        Assert.AreEqual((ReviewSessionFixer.Running, ReviewSessionStatus.Sent), (_store.Sessions[id].SentTo, _store.Sessions[id].Status));
        await fixer.Start(id);

        Assert.AreEqual("fix", sent.Destination);
        Assert.AreEqual(("review-fix-1", "h1"), _t.Worktrees.CheckedOutCommits.Single(), "a checkout of exactly the reviewed commit");
        var turn = _agent.Turns.Single();
        Assert.AreEqual(ThreadTurnKind.ReviewFix, turn.Kind);
        StringAssert.Contains(turn.Prompt, "Page it by key, 5000 at a time.", "what was kept, in the reviewer's words");
        Assert.DoesNotContain("N+1", turn.Prompt, "dropped findings aren't fixed");
        var (path, message) = _t.Worktrees.CommittedAll.Single();
        StringAssert.StartsWith(message, "fix: address review findings (agentd review #1)");
        StringAssert.Contains(message, "- Unbounded read (src/Sync.cs:41)");
        Assert.AreEqual((path, "ai/5617-deploy"), _t.Worktrees.PushedHeads.Single(), "pushed to the PR's own branch");
        var next = _store.Sessions.Values.Single(s => s.Id != id);
        Assert.AreEqual((ReviewTargets.PullRequest, (int?)3944), (next.Target, next.PullRequestId), "round 2: the PR reviewed again");
        Assert.AreEqual($"fix:{next.Id}:c0ffee1", _store.Sessions[id].SentTo);
        var note = _t.PullRequests.Threads.Single();
        StringAssert.StartsWith(note.Text, "🔧 Fixed 1 review finding(s) and 0 comment(s) in c0ffee1");
        Assert.AreEqual(s_dev, _t.PullRequests.ThreadsAs.Single(), "the note under the reviewer's name");
        Assert.AreEqual(path, _t.Worktrees.Removed.Single(), "the checkout is cleaned up");
    }

    [TestMethod]
    public async Task Nothing_changed_or_a_refused_push_says_why_and_never_starts_a_round()
    {
        var unchanged = await ReadyPrReviewAsync();
        var (send, fixer) = Wire();
        _t.Worktrees.NextCommit = null;
        _agent.Reply = new BrainstormReply("Finding 1 is wrong: the table is small.", null, null);
        await send.SendAsync(unchanged, ReviewSessionSend.FixIt, default);
        await fixer.Start(unchanged);

        var refused = await ReadyPrReviewAsync();
        _t.Worktrees.NextCommit = "c0ffee2";
        _t.Worktrees.PushFailure = new InvalidOperationException("! [rejected] (fetch first)");
        await send.SendAsync(refused, ReviewSessionSend.FixIt, default);
        await fixer.Start(refused);

        Assert.AreEqual((ReviewSessionFixer.NoChange, "Finding 1 is wrong: the table is small."), (_store.Sessions[unchanged].SentTo, _store.Sessions[unchanged].Error));
        Assert.AreEqual(ReviewSessionFixer.Failed, _store.Sessions[refused].SentTo);
        StringAssert.Contains(_store.Sessions[refused].Error, "The push to ai/5617-deploy was refused");
        StringAssert.Contains(_store.Sessions[refused].Error, "review it again");
        Assert.HasCount(2, _store.Sessions, "no new rounds");
    }

    [TestMethod]
    public async Task A_commit_range_has_no_branch_to_fix()
    {
        var id = await _store.InsertAsync("sysmin", ReviewTargets.Range, null, "h1", "m1", "dev@example.com", null, null, default);
        await _store.SetStatusAsync(id, ReviewSessionStatus.Ready, null, null, default);

        var result = await Wire().Send.SendAsync(id, ReviewSessionSend.FixIt, default);

        StringAssert.Contains(result.Error!.Message, "Fix it works on a pull request's or a branch's review");
    }

    private async Task<long> ReadyPrReviewAsync()
    {
        var id = await _store.InsertAsync("sysmin", ReviewTargets.PullRequest, 3944, "ai/5617-deploy", "develop", "dev@example.com", null, null, default);
        await _store.PinAsync(id, "m1", "h1", null, default);
        await _store.AddFindingsAsync(id, [
            new SessionFinding("breaks", "src/Sync.cs", 41, "Unbounded read", "Large tables.", "Page by key."),
            new SessionFinding("performance", "src/Lookup.cs", 9, "N+1 lookups", null, null),
        ], "One real bug.", default);
        await _store.DecideAsync(id, 0, FindingDecisions.Edited, "Page it by key, 5000 at a time.", default);
        await _store.DecideAsync(id, 1, FindingDecisions.Dropped, null, default);
        await _store.SetStatusAsync(id, ReviewSessionStatus.Ready, null, null, default);
        _t.PullRequests.Details[3944] = new PullRequestDetails(3944, "Deploy", null, "Dev", "ai/5617-deploy", "develop", "h2", PullRequestStatus.Active, false, new Uri("https://x/pr/3944"));
        _t.Worktrees.Commits["h2"] = "h2";
        _t.Worktrees.Commits["develop"] = "d9";
        _t.Worktrees.MergeBases[("d9", "h2")] = "m1";
        return id;
    }

    private (ReviewSessionSend Send, ReviewSessionFixer Fixer) Wire()
    {
        var onBehalf = new AdoOnBehalf(_connections, new AdoUserTokens(_connections, new FakeProtector(), new FakeDelegation(), _t.Clock), _t.Outbox);
        var sessions = new ReviewSessionService(_store, _t.Registry, _t.Worktrees, _t.PullRequests);
        var fixer = new ReviewSessionFixer(_store, _t.Registry, _t.Worktrees, _agent, _t.PullRequests, sessions, onBehalf, _actor);
        return (new ReviewSessionSend(_store, _t.Registry, _t.PullRequests, onBehalf, _actor, fixer), fixer);
    }

    private sealed class Agent : IBrainstormAgent
    {
        public BrainstormReply Reply { get; set; } = new("- Unbounded read: pages by key now.", null, null);

        public List<BrainstormTurn> Turns { get; } = [];

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            Turns.Add(turn);
            return Task.FromResult(Reply);
        }
    }
}
