using Agentd.Application.Chats;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Persistence.Repositories;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class ChatStoreTests
{
    [TestMethod]
    public async Task A_chat_round_trips_and_only_open_chats_are_polled()
    {
        await using var db = await Database.CreateMigratedAsync("chats_round_trip");
        var store = new ChatStore(db);
        var discord = ProviderKey.From("discord");
        var id = await store.InsertAsync("tngo", discord, "t-1", "guild", ["sysmin", "portal"], default);
        var other = await store.InsertAsync("tngo", discord, "t-2", null, ["sysmin"], default);
        var session = Guid.NewGuid();

        await store.SaveAsync((await store.FindByThreadAsync(discord, "t-1", default))! with
        {
            Session = session,
            Model = "opus",
            Effort = "high",
            Worktrees = ["/wt/sysmin/chat-1", "/wt/portal/chat-1"],
        }, default);
        await store.SaveAsync((await store.GetAsync(other, default))! with { Status = ChatStatus.Closed }, default);
        await store.AddMessageAsync(id, "in", "tngo", "where?", default);
        await store.AddMessageAsync(id, "out", "agent", "here.", default);

        var chat = (await store.GetAsync(id, default))!;
        Assert.AreEqual(("tngo", ChatStatus.Open, (Guid?)session, "opus", "high"), (chat.Author, chat.Status, chat.Session, chat.Model, chat.Effort));
        CollectionAssert.AreEqual(new[] { "sysmin", "portal" }, chat.Repositories.ToList());
        CollectionAssert.AreEqual(new[] { "/wt/sysmin/chat-1", "/wt/portal/chat-1" }, chat.Worktrees.ToList());
        CollectionAssert.AreEqual(new[] { "t-1" }, (await store.ListOpenThreadsAsync(discord, default)).ToList());
        Assert.IsNull(await store.FindByThreadAsync(discord, "nope", default));
    }
}
