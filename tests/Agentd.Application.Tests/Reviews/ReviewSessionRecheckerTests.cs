using System.Collections.Concurrent;
using System.Text;
using Agentd.Application.AzureDevOps;
using Agentd.Application.Ideas;
using Agentd.Application.Ports;
using Agentd.Application.Reviews;
using Microsoft.Extensions.Logging.Abstractions;
using static Agentd.Application.Tests.AzureDevOps.AdoConnectionsTests;
using static Agentd.Application.Tests.Reviews.ReviewSessionServiceTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Reviews;

/// <summary>A review page's Send is re-checked after pushes (review-sessions.md §4), like a chat !review.</summary>
[TestClass]
public sealed class ReviewSessionRecheckerTests
{
    private static readonly Guid s_dev = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly TestContext _t = new();
    private readonly MemoryReviewSessions _store = new();
    private readonly MemoryConnections _connections = new();
    private readonly AdoActor _actor = new();
    private readonly Posts _posts = new();
    private readonly Agent _agent = new();

    private const string FirstFixed = """
        Checked the push.

        ```review-recheck
        {"findings":[{"n":1,"status":"fixed","reply":null},{"n":2,"status":"open","reply":"the migration still has no down script."}],
         "new":[{"severity":"performance","file":"src/Sync.cs","line":70,"title":"Chunk size read per row","detail":"Read it once."}]}
        ```
        """;

    [TestMethod]
    public async Task A_sent_review_is_kept_and_its_main_message_says_its_rechecked()
    {
        var id = await SentAsync();

        var post = _posts.Rows[id];
        Assert.AreEqual((3944, "h1"), (post.PullRequestId, post.Head));
        CollectionAssert.AreEqual(new int?[] { 101, 102 }, post.Posted.Findings.Select(f => f.Thread).ToArray(), "each kept finding with its thread");
        Assert.AreEqual(104, post.Posted.MainThread, "after the comment's thread");
        StringAssert.Contains(_t.PullRequests.Threads.Last().Text, "agentd checks again after every push");
    }

    [TestMethod]
    public async Task A_push_is_rechecked_under_the_reviewers_name_and_the_pr_and_page_follow()
    {
        var id = await SentAsync();
        Push("h2aaaaaa");
        _agent.Replies.Enqueue(FirstFixed);

        await RecheckAsync();

        var turn = _agent.Turns.Single();
        Assert.AreEqual((ThreadTurnKind.ReviewSession, false), (turn.Kind, turn.Resume), "a fresh session on the new head");
        StringAssert.Contains(turn.Prompt, "last checked at h1");
        StringAssert.Contains(turn.Prompt, "Large tables.", "a fresh session gets each finding's text");
        Assert.AreEqual(("review-session-" + id, "h2aaaaaa"), _t.Worktrees.CheckedOutCommits.Last());
        Assert.AreEqual((101, "✅ Fixed in h2aaaaa."), (_t.PullRequests.Replies[0].Thread, _t.PullRequests.Replies[0].Text));
        Assert.AreEqual(PullRequestThreadStatus.Fixed, _t.PullRequests.ThreadStatus[101]);
        Assert.AreEqual((102, "Still open in h2aaaaa: the migration still has no down script."), (_t.PullRequests.Replies[1].Thread, _t.PullRequests.Replies[1].Text));
        StringAssert.StartsWith(_t.PullRequests.Threads.Last().Text, "🟠 **Chunk size read per row**", "a new problem gets its thread");
        Assert.AreEqual(s_dev, _t.PullRequests.ThreadsAs.Last(), "under the reviewer's own name");
        StringAssert.StartsWith(_t.PullRequests.Edited[104], "**Review** · 3 finding(s): 2 open, 1 fixed · checked at h2aaaaa");
        Assert.IsFalse(_t.PullRequests.ThreadStatus.ContainsKey(104), "findings open: the main message stays active");

        var post = _posts.Rows[id];
        Assert.AreEqual("h2aaaaaa", post.Head);
        Assert.AreEqual((1, 2, 0, 1, (string?)null), (post.Rechecks[0].Fixed, post.Rechecks[0].Open, post.Rechecks[0].Closed, post.Rechecks[0].Added, post.Rechecks[0].Problem));

        await RecheckAsync();
        Assert.HasCount(1, _agent.Turns, "nothing new: no second re-check");
    }

    [TestMethod]
    public async Task Everything_fixed_resolves_the_main_message_and_a_reply_brings_the_next_recheck()
    {
        var id = await SentAsync();
        _t.PullRequests.Comments.Add(new PullRequestComment(102, 2, "Dev One", "Added the down script.", "/db/m.sql", 1, "active", DateTimeOffset.UtcNow.AddMinutes(1)));
        _agent.Replies.Enqueue("""
            ```review-recheck
            {"findings":[{"n":1,"status":"fixed"},{"n":2,"status":"closed","reply":"Right, it's reversible now."}]}
            ```
            """);

        await RecheckAsync();

        StringAssert.Contains(_agent.Turns.Single().Prompt, "reply from Dev One: Added the down script.");
        Assert.AreEqual(PullRequestThreadStatus.Closed, _t.PullRequests.ThreadStatus[102]);
        Assert.AreEqual(PullRequestThreadStatus.Fixed, _t.PullRequests.ThreadStatus[104], "nothing open: resolved");
        StringAssert.Contains(_t.PullRequests.Edited[104], "all resolved ✅");
        Assert.IsTrue(ReviewFindings.AllResolved(_posts.Rows[id].Posted));

        Push("h3bbbbbb");
        await RecheckAsync();
        Assert.HasCount(1, _agent.Turns, "all resolved: a push isn't re-checked");
    }

    [TestMethod]
    public async Task A_completed_pr_ends_the_rechecks_and_an_unreadable_reply_waits_for_the_next_push()
    {
        var id = await SentAsync();
        Push("h2aaaaaa");
        _agent.Replies.Enqueue("I looked but forgot the block.");

        await RecheckAsync();

        var problem = _posts.Rows[id].Rechecks.Single();
        StringAssert.Contains(problem.Problem, "review-recheck");
        Assert.AreEqual("h2aaaaaa", _posts.Rows[id].Head, "not tried again at the same head");
        Assert.IsEmpty(_t.PullRequests.Replies);

        _t.PullRequests.Details[3944] = _t.PullRequests.Details[3944] with { Status = PullRequestStatus.Completed, SourceCommit = "h9" };
        await RecheckAsync();
        Assert.IsTrue(_posts.Rows[id].Done);
        Assert.HasCount(1, _agent.Turns);
    }

    [TestMethod]
    public async Task Without_kept_findings_nothing_is_kept_to_recheck()
    {
        var id = await ReadyAsync();
        await _store.DecideAsync(id, 0, FindingDecisions.Dropped, null, default);
        await _store.DecideAsync(id, 2, FindingDecisions.Dropped, null, default);

        await Send().SendAsync(id, ReviewSessionSend.ToPullRequest, default);

        Assert.IsFalse(_posts.Rows.ContainsKey(id));
    }

    private async Task<long> SentAsync()
    {
        var id = await ReadyAsync();
        _connections.Rows.Add(new(s_dev, "dev@example.com", "Dev One", "dev@example.com", Encoding.UTF8.GetBytes("enc:refresh"), false, null, default, default));
        _t.PullRequests.ActingAs = () => _actor.Current;
        _t.PullRequests.Details[3944] = new PullRequestDetails(3944, "Deploy", null, "Dev", "ai/x", "develop", "h1", PullRequestStatus.Active, false, new Uri("https://dev.azure.com/o/p/_git/sysmin/pullrequest/3944"));
        Assert.IsTrue((await Send().SendAsync(id, ReviewSessionSend.ToPullRequest, default)).IsSuccess);
        return id;
    }

    private async Task<long> ReadyAsync()
    {
        var id = await _store.InsertAsync("sysmin", ReviewTargets.PullRequest, 3944, "ai/x", "develop", "dev@example.com", null, null, default);
        await _store.PinAsync(id, "m1", "h1", null, default);
        await _store.AddFindingsAsync(id, [
            new SessionFinding("breaks", "src/Sync.cs", 41, "Unbounded read", "Large tables.", "Page by key."),
            new SessionFinding("performance", "src/Lookup.cs", 9, "N+1 lookups", null, null),
            new SessionFinding("breaks", null, null, "No rollback", "The migration can't be undone.", null),
        ], "One real bug.", default);
        await _store.DecideAsync(id, 1, FindingDecisions.Dropped, null, default);
        await _store.AddCommentAsync(id, "src/Sync.cs", 58, null, "make the size config", "dev@example.com", default);
        await _store.SetStatusAsync(id, ReviewSessionStatus.Ready, null, null, default);
        return id;
    }

    private void Push(string head) => _t.PullRequests.Details[3944] = _t.PullRequests.Details[3944] with { SourceCommit = head };

    /// <summary>One monitor pass, then waits for the re-check it started (if any).</summary>
    private async Task RecheckAsync()
    {
        var turns = _agent.Turns.Count;
        var updates = _posts.Updates;
        await Rechecker().MonitorAsync(default);
        for (var i = 0; i < 200 && _posts.Updates == updates && _agent.Turns.Count == turns; i++)
        {
            await Task.Delay(10);
        }

        for (var i = 0; i < 200 && _agent.Turns.Count > turns && _posts.Updates == updates; i++)
        {
            await Task.Delay(10);
        }
    }

    private ReviewSessionRechecker? _rechecker;

    private ReviewSessionRechecker Rechecker() => _rechecker ??= new(_store, _posts, _t.Registry, _t.PullRequests, _t.Worktrees, _agent, NullLogger<ReviewSessionRechecker>.Instance,
        null, OnBehalf(), _actor);

    private ReviewSessionSend Send() => new(_store, _t.Registry, _t.PullRequests, OnBehalf(), _actor, posts: _posts);

    private AdoOnBehalf OnBehalf() => new(_connections, new AdoUserTokens(_connections, new FakeProtector(), new FakeDelegation(), _t.Clock), _t.Outbox);

    private sealed class Agent : IBrainstormAgent
    {
        public ConcurrentQueue<string> Replies { get; } = new();

        public List<BrainstormTurn> Turns { get; } = [];

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            lock (Turns)
            {
                Turns.Add(turn);
            }

            return Task.FromResult(new BrainstormReply(Replies.TryDequeue(out var r) ? r : "…", null, null));
        }
    }

    private sealed class Posts : IReviewSessionPosts
    {
        public ConcurrentDictionary<long, ReviewSessionPost> Rows { get; } = new();

        public int Updates;

        public Task SetAsync(long sessionId, int pullRequestId, ReviewResult posted, string? head, CancellationToken cancellationToken)
        {
            Rows[sessionId] = new ReviewSessionPost(sessionId, pullRequestId, posted, head, [], false);
            return Task.CompletedTask;
        }

        public Task<ReviewSessionPost?> GetAsync(long sessionId, CancellationToken cancellationToken) => Task.FromResult(Rows.TryGetValue(sessionId, out var p) ? p : null);

        public Task<IReadOnlyList<ReviewSessionPost>> ListOpenAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReviewSessionPost>>([.. Rows.Values.Where(p => !p.Done).OrderBy(p => p.SessionId)]);

        public Task UpdateAsync(long sessionId, ReviewResult posted, string? head, SessionRecheck? recheck, CancellationToken cancellationToken)
        {
            var p = Rows[sessionId];
            Rows[sessionId] = p with { Posted = posted, Head = head, Rechecks = recheck is null ? p.Rechecks : [.. p.Rechecks, recheck] };
            Interlocked.Increment(ref Updates);
            return Task.CompletedTask;
        }

        public Task DoneAsync(long sessionId, CancellationToken cancellationToken)
        {
            Rows[sessionId] = Rows[sessionId] with { Done = true };
            Interlocked.Increment(ref Updates);
            return Task.CompletedTask;
        }
    }
}
