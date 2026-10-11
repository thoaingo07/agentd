using Agentd.Application.Chats;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Persistence.Repositories;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class ChatStoreTests
{
    [TestMethod]
    public async Task Web_chats_have_no_thread_and_list_with_their_first_question_messages_and_events()
    {
        await using var db = await Database.CreateMigratedAsync("chats_web");
        var store = new ChatStore(db);
        var events = new EventStore(db);
        var web = ProviderKey.From("web");
        var from = await events.LatestSeqAsync(default);

        var id = await store.InsertAsync("dev@example.com", web, null, null, ["sysmin", "portal"], default);
        await store.AddMessageAsync(id, "in", "dev@example.com", "what does the sync do?", default);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => store.AddMessageAsync(id, i % 2 == 0 ? "out" : "in", i % 2 == 0 ? "agent" : "dev@example.com", $"m{i}", default))));
        var other = await store.InsertAsync("tngo", ProviderKey.From("discord"), "t-1", null, ["sysmin"], default);

        var list = await store.ListAsync(10, default);
        Assert.AreEqual((other, id), (list[0].Id, list[1].Id), "newest first");
        Assert.AreEqual(("what does the sync do?", 9, false, "Open"), (list[1].Title, list[1].Messages, list[1].InChat, list[1].Status));
        CollectionAssert.AreEqual(new[] { "sysmin", "portal" }, list[1].Repositories.ToList());
        Assert.AreEqual((true, ""), (list[0].InChat, list[0].Title), "no question yet: an empty title");
        var messages = await store.ListMessagesAsync(id, default);
        Assert.HasCount(9, messages, "parallel adds all land");
        Assert.AreEqual(("in", "what does the sync do?"), (messages[0].Direction, messages[0].Text));
        Assert.IsNull((await store.GetAsync(id, default))!.ThreadId);
        Assert.IsEmpty(await store.ListOpenThreadsAsync(web, default), "chat providers never read web chats");
        var announced = (await events.ReadAfterAsync(null, from, 100, default)).Count(e => e.Type == "chat.message");
        Assert.AreEqual(9, announced, "each message is announced (ids only) for the Web UI");
    }

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
