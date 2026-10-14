using Agentd.Application.Reviews;
using Agentd.Infrastructure.Persistence.Repositories;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class ReviewSessionPostStoreTests
{
    private static ReviewResult Posted(string status = ReviewFindings.Open) => new("One real bug.",
        [new ReviewFinding("breaks", "src/Sync.cs", 41, "Unbounded read", "Large tables.", null, 101, status)], 104, DateTimeOffset.UnixEpoch);

    [TestMethod]
    public async Task A_send_is_kept_rechecked_and_ended_and_sending_again_starts_over()
    {
        await using var db = await Database.CreateMigratedAsync("review_session_posts");
        var sessions = new ReviewSessionStore(db);
        var posts = new ReviewSessionPostStore(db);
        var id = await sessions.InsertAsync("sysmin", ReviewTargets.PullRequest, 3944, "ai/x", "develop", "dev@example.com", null, null, default);

        await posts.SetAsync(id, 3944, Posted(), "h1", default);
        await posts.UpdateAsync(id, Posted(ReviewFindings.Fixed), "h2", new SessionRecheck(DateTimeOffset.UnixEpoch, "h2", 1, 0, 0, 0), default);
        await posts.UpdateAsync(id, Posted(ReviewFindings.Fixed), "h2", null, default);   // replies seen moving on: no history entry

        var post = (await posts.GetAsync(id, default))!;
        Assert.AreEqual((3944, "h2", false), (post.PullRequestId, post.Head, post.Done));
        Assert.AreEqual((101, ReviewFindings.Fixed, 104), (post.Posted.Findings[0].Thread, post.Posted.Findings[0].Status, post.Posted.MainThread));
        Assert.AreEqual((1, "h2"), (post.Rechecks.Single().Fixed, post.Rechecks.Single().Head));
        CollectionAssert.AreEqual(new[] { id }, (await posts.ListOpenAsync(default)).Select(p => p.SessionId).ToArray());

        await posts.DoneAsync(id, default);
        await posts.UpdateAsync(id, Posted(), "h3", new SessionRecheck(DateTimeOffset.UnixEpoch, "h3", 0, 1, 0, 0), default);   // too late: ignored
        Assert.IsEmpty(await posts.ListOpenAsync(default));
        Assert.AreEqual("h2", (await posts.GetAsync(id, default))!.Head);

        await posts.SetAsync(id, 3944, Posted(), "h4", default);
        post = (await posts.GetAsync(id, default))!;
        Assert.AreEqual(("h4", false, 0), (post.Head, post.Done, post.Rechecks.Count), "sent again: re-checked again from scratch");
        Assert.IsNull(await posts.GetAsync(id + 1, default));
    }

    [TestMethod]
    public async Task Parallel_rechecks_never_lose_a_history_entry_and_a_deleted_session_takes_its_send()
    {
        await using var db = await Database.CreateMigratedAsync("review_session_posts_parallel");
        var sessions = new ReviewSessionStore(db);
        var posts = new ReviewSessionPostStore(db);
        var id = await sessions.InsertAsync("sysmin", ReviewTargets.PullRequest, 3944, "ai/x", "develop", "dev@example.com", null, null, default);
        await posts.SetAsync(id, 3944, Posted(), "h1", default);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
            posts.UpdateAsync(id, Posted(), $"h{i}", new SessionRecheck(DateTimeOffset.UnixEpoch, $"h{i}", 0, 1, 0, i), default))));

        CollectionAssert.AreEquivalent(Enumerable.Range(0, 16).ToArray(), (await posts.GetAsync(id, default))!.Rechecks.Select(r => r.Added).ToArray());

        await using (var delete = db.CreateCommand("DELETE FROM agentd.review_sessions WHERE id = $1"))
        {
            delete.Parameters.Add(new NpgsqlParameter { Value = id });
            await delete.ExecuteNonQueryAsync();
        }

        Assert.IsNull(await posts.GetAsync(id, default));
    }
}
