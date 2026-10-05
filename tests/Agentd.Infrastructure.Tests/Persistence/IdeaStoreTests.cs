using Agentd.Application.Ideas;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Persistence.Repositories;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class IdeaStoreTests
{
    [TestMethod]
    public async Task An_idea_round_trips_with_its_drafts_settings_and_conversation()
    {
        await using var db = await Database.CreateMigratedAsync("ideas_round_trip");
        var store = new IdeaStore(db);
        var discord = ProviderKey.From("discord");

        var id = await store.InsertAsync("sysmin", "Dark mode", "tngo", discord, "thread-9", "guild-1", default);
        var idea = (await store.FindByThreadAsync(discord, "thread-9", default))!;
        var session = Guid.NewGuid();
        await store.SaveAsync(idea with
        {
            Status = IdeaStatus.Proposed,
            Session = session,
            Model = "opus",
            Effort = "high",
            Worktree = "/wt/idea-1",
            Drafts = [new WorkItemDraft("User Story", "Dark mode", "d", "ac", 5, null, ["ui"]), new WorkItemDraft("Task", "Tokens", null, null, 4, 0, null)],
        }, default);
        await store.AddMessageAsync(id, "in", "tngo", "dark mode please", default);
        await store.AddMessageAsync(id, "out", "agent", "Which pages?", default);

        var loaded = (await store.GetAsync(id, default))!;
        Assert.AreEqual((IdeaStatus.Proposed, (Guid?)session, "opus", "high", "/wt/idea-1"), (loaded.Status, loaded.Session, loaded.Model, loaded.Effort, loaded.Worktree));
        Assert.AreEqual(0, loaded.Drafts![1].Parent);
        CollectionAssert.AreEqual(new[] { "ui" }, loaded.Drafts[0].Tags!.ToArray());
        CollectionAssert.AreEqual(new[] { "in:dark mode please", "out:Which pages?" }, (await store.ListMessagesAsync(id, default)).Select(m => $"{m.Direction}:{m.Text}").ToArray());
        Assert.IsNull(await store.FindByThreadAsync(discord, "thread-other", default));
    }

    [TestMethod]
    public async Task Summaries_count_drafts_and_messages_filter_by_work_item_and_raise_events_without_text()
    {
        await using var db = await Database.CreateMigratedAsync("ideas_summaries");
        var store = new IdeaStore(db);
        var discord = ProviderKey.From("discord");
        var first = await store.InsertAsync("sysmin", "Dark mode", "tngo", discord, "t-1", null, default);
        var second = await store.InsertAsync("sysmin", "Export CSV", "an", discord, "t-2", null, default);
        await store.AddMessageAsync(first, "in", "tngo", "dark mode please", default);
        await store.AddMessageAsync(first, "out", "agentd", "Which pages?", default);
        var idea = (await store.GetAsync(first, default))!;
        await store.SaveAsync(idea with
        {
            Status = IdeaStatus.Created,
            Drafts = [new WorkItemDraft("User Story", "Dark mode", null, null, 3, null, null), new WorkItemDraft("Task", "Tokens", null, null, 2, 0, null)],
            CreatedWorkItems = [5701, 5702],
        }, default);

        var all = await store.ListSummariesAsync(null, null, 10, default);
        var born = await store.ListSummariesAsync(null, 5702, 10, default);

        CollectionAssert.AreEqual(new[] { second, first }, all.Select(i => i.Id).ToArray(), "newest first");
        Assert.AreEqual((IdeaStatus.Created, 2, 2), (all[1].Status, all[1].Drafts, all[1].Messages));
        CollectionAssert.AreEqual(new[] { 5701, 5702 }, all[1].CreatedWorkItems.ToArray());
        Assert.AreEqual(first, born.Single().Id, "the idea a work item was born from");
        Assert.AreEqual(second, (await store.ListSummariesAsync(second, null, 1, default)).Single().Id);

        await using var events = db.CreateCommand("SELECT type, payload::text FROM agentd.events WHERE type LIKE 'idea.%' ORDER BY seq");
        await using var r = await events.ExecuteReaderAsync();
        var seen = new List<string>();
        while (await r.ReadAsync())
        {
            seen.Add(r.GetString(0));
            Assert.DoesNotContain("dark mode please", r.GetString(1), "the conversation isn't copied into the event log");
        }

        CollectionAssert.AreEqual(new[] { "idea.message", "idea.message", "idea.updated" }, seen);
    }
}
