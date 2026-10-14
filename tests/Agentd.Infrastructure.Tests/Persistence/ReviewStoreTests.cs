using Agentd.Application.Reviews;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Persistence.Repositories;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class ReviewStoreTests
{
    [TestMethod]
    public async Task A_review_round_trips_with_its_findings_and_conversation_and_lists_open_threads()
    {
        await using var db = await Database.CreateMigratedAsync("pr_reviews_round_trip");
        var store = new ReviewStore(db);
        var discord = ProviderKey.From("discord");
        var id = await store.InsertAsync("sysmin", 3935, "Health checks", "tngo", discord, "t-1", "guild", default);
        var closed = await store.InsertAsync("sysmin", 3936, "Old", "tngo", discord, "t-2", null, default);
        var review = (await store.FindByThreadAsync(discord, "t-1", default))!;
        var session = Guid.NewGuid();
        var result = new ReviewResult("Adds health checks.", [new ReviewFinding("major", "src/Health.cs", 42, "Readiness never fails", "why", "fix")]);

        await store.SaveAsync(review with
        {
            Status = ReviewStatus.Posted,
            HeadCommit = "4c1e1a7",
            Focus = "security",
            Session = session,
            Model = "opus",
            Effort = "high",
            Worktree = "/wt/review-1",
            Result = result,
            PostedThreads = [77, 78],
        }, default);
        await store.SaveAsync((await store.GetAsync(closed, default))! with { Status = ReviewStatus.Closed }, default);
        await store.AddMessageAsync(id, "in", "tngo", "why is 1 a problem?", default);
        await store.AddMessageAsync(id, "out", "agent", "Because …", default);

        var loaded = (await store.GetAsync(id, default))!;
        Assert.AreEqual((3935, ReviewStatus.Posted, "4c1e1a7", "security", (Guid?)session, "opus", "high"),
            (loaded.PullRequestId, loaded.Status, loaded.HeadCommit, loaded.Focus, loaded.Session, loaded.Model, loaded.Effort));
        Assert.AreEqual(("Readiness never fails", (int?)42), (loaded.Result!.Findings.Single().Title, loaded.Result.Findings[0].Line));
        CollectionAssert.AreEqual(new[] { 77, 78 }, loaded.PostedThreads.ToArray());
        CollectionAssert.AreEqual(new[] { "in", "out" }, (await store.ListMessagesAsync(id, default)).Select(m => m.Direction).ToArray());
        CollectionAssert.AreEqual(new[] { "t-1" }, (await store.ListOpenThreadsAsync(discord, default)).ToArray(), "closed reviews aren't read");

        await using var events = db.CreateCommand("SELECT count(*) FROM agentd.events WHERE type LIKE 'review.%' AND payload::text LIKE '%why is 1%'");
        Assert.AreEqual(0L, (long)(await events.ExecuteScalarAsync())!, "the conversation isn't copied into the event log");
    }

    [TestMethod]
    public async Task The_latest_review_of_a_pr_is_found()
    {
        await using var db = await Database.CreateMigratedAsync("pr_reviews_latest");
        var store = new ReviewStore(db);
        var discord = ProviderKey.From("discord");
        await store.InsertAsync("sysmin", 3935, "First", "tngo", discord, "t-1", null, default);
        var latest = await store.InsertAsync("sysmin", 3935, "Again", "tngo", discord, "t-2", null, default);
        await store.InsertAsync("other", 3935, "Same number, other repo", "tngo", discord, "t-3", null, default);

        Assert.AreEqual(latest, (await store.FindLatestAsync("sysmin", 3935, default))!.Id);
        Assert.IsNull(await store.FindLatestAsync("sysmin", 1, default));

        var posted = (await store.GetAsync(latest, default))! with { Status = ReviewStatus.Posted };
        await store.SaveAsync(posted, default);
        CollectionAssert.AreEqual(new[] { latest }, (await store.ListPostedAsync(default)).Select(r => r.Id).ToList(), "only posted reviews are re-checked");
    }

    [TestMethod]
    public async Task Reviews_are_listed_by_status_oldest_first_and_parallel_readers_agree()
    {
        await using var db = await Database.CreateMigratedAsync("pr_reviews_by_status");
        var store = new ReviewStore(db);
        var discord = ProviderKey.From("discord");
        var first = await store.InsertAsync("sysmin", 1, "One", "tngo", discord, "t-1", null, default);
        var posted = await store.InsertAsync("sysmin", 2, "Two", "tngo", discord, "t-2", null, default);
        var second = await store.InsertAsync("portal-api", 3, "Three", "tngo", discord, "t-3", null, default);
        await store.SaveAsync((await store.GetAsync(posted, default))! with { Status = ReviewStatus.Posted }, default);

        CollectionAssert.AreEqual(new[] { first, second }, (await store.ListByStatusAsync(ReviewStatus.Reviewing, default)).Select(r => r.Id).ToList());
        CollectionAssert.AreEqual(new[] { posted }, (await store.ListByStatusAsync(ReviewStatus.Posted, default)).Select(r => r.Id).ToList());
        Assert.IsEmpty(await store.ListByStatusAsync(ReviewStatus.Closed, default));

        var lists = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => store.ListByStatusAsync(ReviewStatus.Reviewing, default)));
        Assert.IsTrue(lists.All(l => l.Select(r => r.Id).SequenceEqual([first, second])), "each caller its own connection, same answer");
    }
}
