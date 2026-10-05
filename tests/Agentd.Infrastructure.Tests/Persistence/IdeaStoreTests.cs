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
}
