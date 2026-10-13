using Agentd.Application.Ideas;
using Agentd.Application.Reviews;
using static Agentd.Application.Tests.Reviews.ReviewSessionServiceTests;
using TestContext = Agentd.Application.Tests.Fakes.TestContext;

namespace Agentd.Application.Tests.Reviews;

[TestClass]
public sealed class ReviewSessionReviewerTests
{
    private const string Findings = """
        One real bug.

        ```review-findings
        {"summary":"Pages the sync by key. One real bug.","findings":[
          {"severity":"breaks","file":"src/Sync.cs","line":41,"title":"Unbounded read of the whole table","detail":"Large tables load at once.","suggestion":"Page by the primary key."},
          {"severity":"performance","file":"src/Lookup.cs","line":12,"title":"N+1 lookups"}]}
        ```
        """;

    private readonly TestContext _t = new();
    private readonly MemoryReviewSessions _store = new();
    private readonly Agent _agent = new();

    [TestMethod]
    public async Task The_reviewer_reads_the_pinned_head_and_its_findings_make_the_session_ready()
    {
        var id = await ReviewingAsync();
        _agent.Reply = new BrainstormReply(Findings, null, null);

        await Reviewer().RunAsync(id, default);

        var s = _store.Sessions[id];
        Assert.AreEqual((ReviewSessionStatus.Ready, "Pages the sync by key. One real bug."), (s.Status, s.Summary));
        CollectionAssert.AreEqual(new[] { "Unbounded read of the whole table", "N+1 lookups" }, s.Findings.Select(f => f.Title).ToList());
        Assert.AreEqual(("review-session-1", "h1"), _t.Worktrees.CheckedOutCommits.Single(), "a read-only checkout of the pinned head");
        Assert.AreEqual("/home/agentd/.agentd/worktrees/sysmin/review-session-1", s.Worktree);
        var turn = _agent.Turns.Single();
        Assert.AreEqual((ThreadTurnKind.ReviewSession, "claude-opus-5-5", "high"), (turn.Kind, turn.Model, turn.Effort));
        StringAssert.Contains(turn.Prompt, "git diff m1 h1");
        StringAssert.Contains(turn.Prompt, "the branch `feature/keyset` against `develop` (not a pull request)");
    }

    [TestMethod]
    public async Task The_reviewer_is_asked_for_a_guide_and_it_is_kept_fitted_to_the_changed_files()
    {
        var id = await ReviewingAsync();
        _t.Worktrees.ChangedFiles.Clear();
        _t.Worktrees.ChangedFiles.AddRange(["src/Sync.cs", "src/Lookup.cs", "README.md"]);
        _agent.Reply = new BrainstormReply(Findings + """

            ```review-guide
            {"overview":"Reads the table in keyset chunks instead of all at once.",
             "chapters":[{"title":"Keyset paging","why":"The heart: the read loop.","files":[{"path":"src/Sync.cs","summary":"Pages by key."}]},
                         {"title":"Lookups","files":[{"path":"/src/Lookup.cs","summary":"Caches names."},{"path":"src/Gone.cs","summary":"not changed"}]}],
             "notes":[{"file":"src/Sync.cs","line":41,"text":"The cursor is the last key read."}],
             "suggestions":[{"file":"src/Sync.cs","line":58,"title":"Make the chunk size configurable","suggestion":"Read it from options."}]}
            ```
            """, null, null);

        await Reviewer().RunAsync(id, default);

        var guide = _store.Sessions[id].Guide!;
        StringAssert.Contains(_agent.Turns.Single().Prompt, "```review-guide");
        Assert.AreEqual("Reads the table in keyset chunks instead of all at once.", guide.Overview);
        CollectionAssert.AreEqual(new[] { "Keyset paging", "Lookups", ReviewGuides.Rest }, guide.Chapters.Select(c => c.Title).ToList());
        CollectionAssert.AreEqual(new[] { "src/Lookup.cs" }, guide.Chapters[1].Files.Select(f => f.Path).ToList(), "unchanged files dropped, paths cleaned");
        Assert.AreEqual(("README.md", (string?)null), (guide.Chapters[2].Files.Single().Path, guide.Chapters[2].Files.Single().Summary), "what the reviewer left out");
        Assert.AreEqual(("The cursor is the last key read.", "Make the chunk size configurable"), (guide.Notes!.Single().Text, guide.Suggestions!.Single().Title));
        Assert.AreEqual(ReviewSessionStatus.Ready, _store.Sessions[id].Status);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("\n```review-guide\n{\"overview\":\"\",\"chapters\":[]}\n```")]
    public async Task A_missing_or_unreadable_guide_never_fails_the_review(string guide)
    {
        var id = await ReviewingAsync();
        _agent.Reply = new BrainstormReply(Findings + guide, null, null);

        await Reviewer().RunAsync(id, default);

        Assert.AreEqual((ReviewSessionStatus.Ready, (ReviewGuide?)null), (_store.Sessions[id].Status, _store.Sessions[id].Guide));
    }

    [TestMethod]
    [DataRow("no block", "couldn't be read")]
    [DataRow(null, "didn't answer")]
    public async Task A_reply_without_findings_fails_the_session_with_why(string? text, string expected)
    {
        var id = await ReviewingAsync();
        _agent.Reply = new BrainstormReply(text, null, text is null ? "exit 1" : null);

        await Reviewer().RunAsync(id, default);

        Assert.AreEqual(ReviewSessionStatus.Failed, _store.Sessions[id].Status);
        StringAssert.Contains(_store.Sessions[id].Error, expected);
    }

    [TestMethod]
    public async Task A_usage_limit_says_when_to_try_again()
    {
        var id = await ReviewingAsync();
        _agent.Reply = new BrainstormReply(null, new DateTimeOffset(2026, 10, 9, 15, 30, 0, TimeSpan.Zero), null);

        await Reviewer().RunAsync(id, default);

        StringAssert.Contains(_store.Sessions[id].Error, "until 15:30 UTC");
    }

    [TestMethod]
    public async Task Starting_a_review_runs_it_once_and_a_restart_picks_up_what_was_left_reviewing()
    {
        _agent.Reply = new BrainstormReply(Findings, null, null);
        var reviewer = Reviewer();
        var service = ServiceWith(reviewer);
        _t.Worktrees.Commits["feature/keyset"] = "h1";
        _t.Worktrees.Commits["develop"] = "d9";
        _t.Worktrees.MergeBases[("d9", "h1")] = "m1";

        var started = (await service.StartAsync(new StartReview("sysmin", Branch: "feature/keyset"), "dev@example.com", default)).Value!;
        await reviewer.Start(started.Id);
        var leftOver = await _store.InsertAsync("sysmin", ReviewTargets.Range, null, "h1", "m1", "dev@example.com", null, null, default);
        await _store.PinAsync(leftOver, "m1", "h1", null, default);
        var chat = await _store.InsertAsync("sysmin", ReviewTargets.PullRequest, 3944, "ai/deploy", "develop", "Kelvin", null, null, default);
        await _store.PinAsync(chat, "m1", "h1", null, default);
        await _store.LinkChatAsync(chat, new Uri("https://chat.example/t1"), 7, default);
        await reviewer.ResumeAsync(default);
        await WaitUntil(() => _store.Sessions[leftOver].Status == ReviewSessionStatus.Ready);

        Assert.AreEqual(ReviewSessionStatus.Reviewing, started.Status, "it starts reviewing");
        Assert.AreEqual(ReviewSessionStatus.Ready, _store.Sessions[started.Id].Status);
        Assert.HasCount(2, _agent.Turns, "one turn each, never twice for the same session");
        Assert.AreEqual(ReviewSessionStatus.Reviewing, _store.Sessions[chat].Status, "a chat review's session is reviewed in its thread");
    }

    private async Task<long> ReviewingAsync()
    {
        var id = await _store.InsertAsync("sysmin", ReviewTargets.Branch, null, "feature/keyset", "develop", "dev@example.com", "claude-opus-5-5", "high", default);
        await _store.PinAsync(id, "m1", "h1", null, default);
        return id;
    }

    private ReviewSessionReviewer Reviewer() => new(_store, _t.Registry, _t.Worktrees, _agent);

    private ReviewSessionService ServiceWith(ReviewSessionReviewer reviewer) => new(_store, _t.Registry, _t.Worktrees, _t.PullRequests, null, reviewer);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, "timed out");
            await Task.Delay(10);
        }
    }

    private sealed class Agent : IBrainstormAgent
    {
        public BrainstormReply Reply { get; set; } = new(null, null, null);

        public List<BrainstormTurn> Turns { get; } = [];

        public Task<BrainstormReply> RunAsync(BrainstormTurn turn, CancellationToken cancellationToken)
        {
            lock (Turns)
            {
                Turns.Add(turn);
            }

            return Task.FromResult(Reply);
        }
    }
}
