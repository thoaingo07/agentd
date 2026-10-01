using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;

namespace Agentd.Domain.Tests.Messaging;

[TestClass]
public sealed class ConversationTests
{
    private static readonly DateTimeOffset s_now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly JobId s_job = new(7);
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");

    [TestMethod]
    [DataRow("discord")]
    [DataRow("telegram-team")]
    [DataRow("p2")]
    public void Valid_provider_keys(string value) => Assert.IsTrue(ProviderKey.Create(value).IsSuccess);

    [TestMethod]
    [DataRow("")]
    [DataRow(null)]
    [DataRow("Discord")]
    [DataRow("my provider")]
    [DataRow("-discord")]
    [DataRow("discord-")]
    [DataRow("disc--ord")]
    [DataRow("discord_1")]
    public void Invalid_provider_keys_are_rejected(string? value) =>
        Assert.AreEqual("validation", ProviderKey.Create(value).Error?.Code);

    [TestMethod]
    public void Opening_raises_ConversationOpened()
    {
        var conversation = Conversation.Open(s_job, s_discord, "123", "guild", new Uri("https://discord.com/channels/1/123"), [], s_now).Value!;

        Assert.IsTrue(conversation.IsOpen);
        var opened = (ConversationOpened)conversation.DequeueEvents().Single();
        Assert.AreEqual("123", opened.ExternalConversationId);
    }

    [TestMethod]
    public void A_second_open_conversation_on_the_same_provider_is_rejected()
    {
        var first = Conversation.Open(s_job, s_discord, "123", null, null, [], s_now).Value!;

        var second = Conversation.Open(s_job, s_discord, "456", null, null, [first], s_now);

        Assert.AreEqual("conflict", second.Error?.Code);
    }

    [TestMethod]
    public void Another_provider_or_a_closed_conversation_is_fine()
    {
        var first = Conversation.Open(s_job, s_discord, "123", null, null, [], s_now).Value!;

        Assert.IsTrue(Conversation.Open(s_job, ProviderKey.From("telegram"), "-100:5", null, null, [first], s_now).IsSuccess);
        first.Close(s_now);
        Assert.IsTrue(Conversation.Open(s_job, s_discord, "456", null, null, [first], s_now).IsSuccess);
    }

    [TestMethod]
    public void Closing_twice_is_rejected()
    {
        var conversation = Conversation.Open(s_job, s_discord, "123", null, null, [], s_now).Value!;

        Assert.IsTrue(conversation.Close(s_now).IsSuccess);
        Assert.IsFalse(conversation.Close(s_now).IsSuccess);
    }
}
