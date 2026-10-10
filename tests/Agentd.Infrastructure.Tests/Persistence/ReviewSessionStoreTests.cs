using Agentd.Application.Reviews;
using Agentd.Infrastructure.Persistence.Repositories;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class ReviewSessionStoreTests
{
    private static SessionFinding Finding(int n, string severity = "breaks") => new(severity, $"src/F{n}.cs", n * 10, $"Finding {n}", "why", "fix");

    [TestMethod]
    public async Task A_session_is_pinned_findings_stream_in_and_it_ends_sent()
    {
        await using var db = await Database.CreateMigratedAsync("review_sessions_round_trip");
        var store = new ReviewSessionStore(db);

        var id = await store.InsertAsync("sysmin", ReviewTargets.Branch, null, "feature/keyset", "develop", "dev@example.com", "claude-opus-5-5", "high", default);
        await store.PinAsync(id, "a1b2c3d", "f9e8d7c", "/wt/sysmin/review-1", default);
        var first = await store.AddFindingsAsync(id, [Finding(1)], null, default);
        var second = await store.AddFindingsAsync(id, [Finding(2, "performance"), Finding(3)], "One real bug.", default);
        await store.SetStatusAsync(id, ReviewSessionStatus.Ready, null, null, default);
        await store.SetStatusAsync(id, ReviewSessionStatus.Sent, null, "pr:3944", default);

        var s = (await store.GetAsync(id, default))!;
        Assert.AreEqual((1, 3), (first, second), "appended, never replaced");
        Assert.AreEqual(("sysmin", "branch", "feature/keyset", "develop", "a1b2c3d", "f9e8d7c"), (s.Repository, s.Target, s.HeadRef, s.BaseRef, s.BaseCommit, s.HeadCommit));
        Assert.AreEqual((ReviewSessionStatus.Sent, "pr:3944", "One real bug.", "/wt/sysmin/review-1"), (s.Status, s.SentTo, s.Summary, s.Worktree));
        CollectionAssert.AreEqual(new[] { "Finding 1", "Finding 2", "Finding 3" }, s.Findings.Select(f => f.Title).ToList());
        Assert.IsTrue(s.Findings.All(f => f.Decision == FindingDecisions.Kept), "kept unless someone decides otherwise");
        Assert.AreEqual(id, (await store.ListAsync("dev@example.com", 10, default)).Single().Id);
        Assert.IsEmpty(await store.ListAsync("someone@else", 10, default));
    }

    [TestMethod]
    public async Task Parallel_decisions_on_different_findings_all_stick()
    {
        await using var db = await Database.CreateMigratedAsync("review_sessions_decisions");
        var store = new ReviewSessionStore(db);
        var id = await store.InsertAsync("sysmin", ReviewTargets.PullRequest, 3944, null, null, "dev@example.com", null, null, default);
        await store.AddFindingsAsync(id, [.. Enumerable.Range(1, 12).Select(n => Finding(n))], null, default);

        // Twelve people's clicks at once, each on its own finding, each with its own connection.
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => (i % 3) switch
        {
            0 => store.DecideAsync(id, i, FindingDecisions.Dropped, null, default),
            1 => store.DecideAsync(id, i, FindingDecisions.Edited, $"say it like this {i}", default),
            _ => store.DecideAsync(id, i, FindingDecisions.Kept, null, default),
        })));
        var outOfRange = await store.DecideAsync(id, 12, FindingDecisions.Dropped, null, default);

        var findings = (await store.GetAsync(id, default))!.Findings;
        Assert.IsTrue(results.All(r => r));
        Assert.IsFalse(outOfRange);
        for (var i = 0; i < 12; i++)
        {
            var expected = i % 3 == 0 ? FindingDecisions.Dropped : i % 3 == 1 ? FindingDecisions.Edited : FindingDecisions.Kept;
            Assert.AreEqual(expected, findings[i].Decision, $"finding {i}");
            Assert.AreEqual(i % 3 == 1 ? $"say it like this {i}" : null, findings[i].Edited);
            Assert.AreEqual($"Finding {i + 1}", findings[i].Title, "the finding itself is untouched");
        }
    }

    [TestMethod]
    public async Task Comments_and_questions_belong_to_their_session_and_only_authors_remove_comments()
    {
        await using var db = await Database.CreateMigratedAsync("review_sessions_comments");
        var store = new ReviewSessionStore(db);
        var id = await store.InsertAsync("sysmin", ReviewTargets.Range, null, "f9e8d7c", "a1b2c3d", "dev@example.com", null, null, default);
        var other = await store.InsertAsync("sysmin", ReviewTargets.Branch, null, "x", "develop", "dev@example.com", null, null, default);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() => store.AddCommentAsync(id, "src/A.cs", i, i + 2, $"note {i}", "dev@example.com", default))));
        var whole = await store.AddCommentAsync(id, null, null, null, "overall fine", "lead@example.com", default);
        var notMine = await store.DeleteCommentAsync(id, whole, "dev@example.com", default);
        var wrongSession = await store.DeleteCommentAsync(other, whole, "lead@example.com", default);
        var mine = await store.DeleteCommentAsync(id, whole, "lead@example.com", default);
        var ask = await store.AddAskAsync(id, "src/A.cs", 3, 5, "why a loop here?", "dev@example.com", null, default);
        await store.AnswerAsync(ask, "It pages by key.", default);

        var comments = await store.ListCommentsAsync(id, default);
        Assert.HasCount(10, comments, "parallel adds all land");
        Assert.AreEqual((false, false, true), (notMine, wrongSession, mine));
        Assert.IsEmpty(await store.ListCommentsAsync(other, default));
        var q = (await store.ListAsksAsync(id, default)).Single();
        Assert.AreEqual(("why a loop here?", "It pages by key.", 3, 5), (q.Question, q.Answer, q.Line, q.EndLine));
        Assert.IsNotNull(q.AnsweredAt);
    }

    [TestMethod]
    public async Task A_follow_up_joins_its_thread_at_its_place_only_after_the_last_answer_and_one_of_parallel_follow_ups_wins()
    {
        await using var db = await Database.CreateMigratedAsync("review_ask_threads");
        var store = new ReviewSessionStore(db);
        var id = await store.InsertAsync("sysmin", ReviewTargets.Branch, null, "x", "develop", "dev@example.com", null, null, default);
        var other = await store.InsertAsync("sysmin", ReviewTargets.Branch, null, "y", "develop", "dev@example.com", null, null, default);
        var root = await store.AddAskAsync(id, "src/A.cs", 3, 5, "why a loop here?", "dev@example.com", null, default);

        var tooSoon = await store.AddAskAsync(id, null, null, null, "and if it's empty?", "dev@example.com", root, default);
        await store.AnswerAsync(root, "It pages by key.", default);
        var wrongSession = await store.AddAskAsync(other, null, null, null, "?", "dev@example.com", root, default);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => store.AddAskAsync(id, null, null, null, $"follow-up {i}", "dev@example.com", root, default))));
        var followUpOfFollowUp = await store.AddAskAsync(id, null, null, null, "?", "dev@example.com", parallel.Max(), default);
        var fresh = Guid.NewGuid();
        await store.SetAskSessionAsync(root, fresh, default);

        Assert.AreEqual((IReviewSessionStore.ThreadBusy, IReviewSessionStore.NoThread, IReviewSessionStore.NoThread), (tooSoon, wrongSession, followUpOfFollowUp));
        Assert.HasCount(1, parallel.Where(a => a > 0), "one follow-up lands; the others wait for its answer");
        Assert.HasCount(7, parallel.Where(a => a == IReviewSessionStore.ThreadBusy));
        var asks = await store.ListAsksAsync(id, default);
        Assert.HasCount(2, asks);
        Assert.AreEqual((null, fresh), (asks[0].ThreadId, asks[0].AgentSession));
        Assert.AreEqual((root, "src/A.cs", 3, 5, (Guid?)null), (asks[1].ThreadId, asks[1].File, asks[1].Line, asks[1].EndLine, asks[1].AgentSession));
    }
}
