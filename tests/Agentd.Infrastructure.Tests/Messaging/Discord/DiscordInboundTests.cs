using System.Text.Json.Nodes;
using Agentd.Application.Messaging;
using Agentd.Application.Ports;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Domain.Messaging;
using Agentd.Infrastructure.Messaging.Discord;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Infrastructure.Tests.Messaging.Discord;

[TestClass]
public sealed class DiscordInboundTests
{
    private static readonly IReadOnlyList<MessageOption> s_options = [new("opt1", "v1"), new("opt2", "v2")];

    [TestMethod]
    public void A_thread_reply_maps_to_an_inbound_message()
    {
        var inbound = DiscordInbound.Map(Message("101", "use v2 please", "710392908099878953", "Thoai"), "t1", "!", null)!;

        Assert.AreEqual(("discord", "101", "t1", "710392908099878953", "Thoai"), (inbound.Provider.Value, inbound.ExternalMessageId, inbound.ExternalConversationId, inbound.ExternalUserId, inbound.UserDisplayName));
        Assert.AreEqual("use v2 please", inbound.Text);
        Assert.IsNull(inbound.Command);
        Assert.AreEqual(new DateTimeOffset(2026, 10, 3, 9, 0, 0, TimeSpan.Zero), inbound.SentAt);
    }

    [TestMethod]
    public void Bots_and_empty_messages_are_ignored()
    {
        Assert.IsNull(DiscordInbound.Map(Message("1", "hi", "9", "agentd", bot: true), "t1", "!", null));
        Assert.IsNull(DiscordInbound.Map(Message("2", "  ", "9", "x"), "t1", "!", null));
    }

    [TestMethod]
    [DataRow("!status", "status", "")]
    [DataRow("!RUN #1234", "run", "#1234")]
    [DataRow("!run   1234  ", "run", "1234")]
    public void Typed_commands_become_neutral_commands(string content, string name, string args)
    {
        var command = DiscordInbound.Map(Message("3", content, "9", "x"), "t1", "!", null)!.Command!;

        Assert.AreEqual(name, command.Name);
        Assert.AreEqual(args, string.Join(' ', command.Args));
    }

    [TestMethod]
    [DataRow("2", "opt2", "v2")]
    [DataRow("1.", "opt1", "v1")]
    [DataRow("3", null, "3")]
    [DataRow("! nope", null, "! nope")]
    public void A_number_answers_the_last_options(string content, string? option, string text)
    {
        var inbound = DiscordInbound.Map(Message("4", content, "9", "x"), "t1", "!", s_options)!;

        Assert.AreEqual(option, inbound.SelectedOptionId);
        Assert.AreEqual(text, inbound.Text);
    }

    [TestMethod]
    public async Task The_poller_replays_threads_skips_old_parent_commands_and_only_takes_new_messages()
    {
        using var discord = new DiscordProviderTests.FakeDiscord();
        discord.Respond(HttpMethod.Get, "channels/555/messages?limit=1", $"[{Raw("50", "!list")}]");
        discord.Respond(HttpMethod.Get, "channels/555/messages", "[]");
        discord.Respond(HttpMethod.Get, "channels/t1/messages", $"[{Raw("12", "second")},{Raw("11", "first")},{Raw("10", "from the bot", bot: true)}]");
        var sink = new RecordingSink();
        var poller = Poller(discord, sink, out var options);

        await poller.PollOnceAsync(default);
        CollectionAssert.AreEqual(new[] { "first", "second" }, sink.Received.Select(m => m.Text).ToArray(), "thread replayed oldest first, bots skipped");

        discord.Respond(HttpMethod.Get, "channels/555/messages", $"[{Raw("60", "!run 7")},{Raw("59", "just chatting")}]");
        discord.Respond(HttpMethod.Get, "channels/t1/messages", "[]");
        await poller.PollOnceAsync(default);

        var parentRun = sink.Received.Single(m => m.Command is not null);
        Assert.AreEqual(("run", "555"), (parentRun.Command!.Name, parentRun.ExternalConversationId));
        Assert.IsFalse(sink.Received.Any(m => m.Command?.Name == "list"), "commands from before startup never run");
        Assert.IsFalse(sink.Received.Any(m => m.Text == "just chatting"), "only commands in the parent channel");
        Assert.IsTrue(discord.RequestUris.Last(u => u.Contains("channels/t1/", StringComparison.Ordinal)).Contains("after=12", StringComparison.Ordinal));
    }

    private static DiscordPoller Poller(DiscordProviderTests.FakeDiscord discord, RecordingSink sink, out DiscordOptions options)
    {
        options = new DiscordOptions { Enabled = true, BotToken = "t", GuildId = "777", ChannelId = "555" };
        var wrapped = Microsoft.Extensions.Options.Options.Create(options);
        var http = new HttpClient(discord) { BaseAddress = new Uri("https://discord.test/api/v10/") };
        var rest = new DiscordRest(() => http);
        var provider = new DiscordMessagingProvider(rest, wrapped);
        var conversation = Conversation.Open(new JobId(1), provider.Key, "t1", "777", null, [], DateTimeOffset.UtcNow).Value!;
        var services = new ServiceCollection()
            .AddSingleton<IInboundMessageSink>(sink)
            .AddSingleton<IConversationStore>(new OneConversation(conversation))
            .BuildServiceProvider();
        return new DiscordPoller(rest, provider, services.GetRequiredService<IServiceScopeFactory>(), wrapped, NullLogger<DiscordPoller>.Instance);
    }

    private static string Raw(string id, string content, bool bot = false) =>
        new JsonObject
        {
            ["id"] = id,
            ["content"] = content,
            ["author"] = new JsonObject { ["id"] = "9", ["username"] = "x", ["bot"] = bot },
        }.ToJsonString();

    private static JsonObject Message(string id, string content, string authorId, string name, bool bot = false) =>
        new JsonObject
        {
            ["id"] = id,
            ["content"] = content,
            ["timestamp"] = "2026-10-03T09:00:00+00:00",
            ["author"] = new JsonObject { ["id"] = authorId, ["username"] = "u", ["global_name"] = name, ["bot"] = bot },
        };

    private sealed class RecordingSink : IInboundMessageSink
    {
        public List<InboundMessage> Received { get; } = [];

        public Task HandleAsync(InboundMessage message, CancellationToken cancellationToken)
        {
            Received.Add(message);
            return Task.CompletedTask;
        }
    }

    private sealed class OneConversation(Conversation conversation) : IConversationStore
    {
        public Task<IReadOnlyList<Conversation>> ListOpenAsync(ProviderKey provider, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Conversation>>([conversation]);

        public Task<Domain.Common.Result> AddAsync(Conversation c, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Domain.Common.Result> SaveAsync(Conversation c, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Conversation>> ListByJobAsync(JobId jobId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Conversation?> FindExternalAsync(ProviderKey provider, string externalConversationId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
