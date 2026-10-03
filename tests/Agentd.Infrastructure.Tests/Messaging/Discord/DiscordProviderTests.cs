using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Agentd.Application.Messaging;
using Agentd.Domain.Jobs.ValueObjects;
using Agentd.Infrastructure.Messaging.Discord;

namespace Agentd.Infrastructure.Tests.Messaging.Discord;

[TestClass]
public sealed class DiscordProviderTests : IDisposable
{
    private readonly FakeDiscord _discord = new();

    [TestMethod]
    public async Task Opening_posts_a_starter_and_creates_a_named_thread()
    {
        _discord.Respond(HttpMethod.Post, "channels/555/messages", """{"id":"m1"}""");
        _discord.Respond(HttpMethod.Post, "channels/555/messages/m1/threads", """{"id":"t1"}""");
        var title = new string('x', 200);

        var conversation = await Provider().OpenConversationAsync(
            new ConversationSpec(new JobId(7), WorkItemId.From(1234), title, RepositoryName.From("sysmin"), new OutboundMessage(MessageKind.Info, "hi")), default);

        Assert.AreEqual("t1", conversation.ExternalConversationId);
        var thread = _discord.Body(1);
        var name = thread["name"]!.GetValue<string>();
        StringAssert.StartsWith(name, "WI-1234 · xxx");
        Assert.AreEqual(100, name.Length);
        Assert.AreEqual(10080, thread["auto_archive_duration"]!.GetValue<int>());
        Assert.AreEqual(new Uri("https://discord.com/channels/777/t1"), Provider().GetLink(conversation));
    }

    [TestMethod]
    public async Task Every_message_disables_mentions_and_options_become_numbers()
    {
        _discord.Respond(HttpMethod.Post, "channels/t1/messages", """{"id":"m2"}""");
        var provider = Provider();
        var thread = new ConversationRef(provider.Key, "t1", "777");

        var sent = await provider.SendAsync(thread, new OutboundMessage(MessageKind.Question, "@everyone v1 or v2?", [new MessageOption("opt1", "v1"), new MessageOption("opt2", "v2")]), default);

        Assert.AreEqual("m2", sent.ExternalMessageId);
        var body = _discord.Body(0);
        Assert.AreEqual(0, body["allowed_mentions"]!["parse"]!.AsArray().Count, "nothing ever pings");
        var content = body["content"]!.GetValue<string>();
        StringAssert.Contains(content, "@​everyone");
        StringAssert.Contains(content, "Reply with a number:\n1. v1\n2. v2");
        Assert.AreEqual("opt2", provider.LastOptions("t1")![1].Id);
        Assert.AreEqual("Bot test-token", _discord.Requests[0].Auth);
    }

    [TestMethod]
    public async Task Attachments_go_as_multipart_files()
    {
        _discord.Respond(HttpMethod.Post, "channels/t1/messages", """{"id":"m3"}""");
        var provider = Provider();

        await provider.SendAsync(new ConversationRef(provider.Key, "t1", null),
            new OutboundMessage(MessageKind.Info, "log", null, [new Attachment("log.txt", "text/plain", Encoding.UTF8.GetBytes("hello"))]), default);

        var request = _discord.Requests.Single();
        StringAssert.StartsWith(request.ContentType, "multipart/form-data");
        StringAssert.Contains(request.Body, "payload_json");
        StringAssert.Contains(request.Body, "filename=log.txt");
        StringAssert.Contains(request.Body, "hello");
    }

    [TestMethod]
    public async Task Rate_limits_are_transient_with_retry_after_and_missing_access_is_permanent()
    {
        var provider = Provider();
        var thread = new ConversationRef(provider.Key, "t1", null);
        _discord.Respond(HttpMethod.Post, "channels/t1/messages", """{"message":"You are being rate limited.","retry_after":1.5}""", HttpStatusCode.TooManyRequests);

        var limited = await Assert.ThrowsExactlyAsync<MessagingDeliveryException>(() => provider.SendAsync(thread, new OutboundMessage(MessageKind.Info, "x"), default));
        Assert.IsFalse(limited.Permanent);
        Assert.AreEqual(TimeSpan.FromSeconds(1.5), limited.RetryAfter);

        _discord.Respond(HttpMethod.Post, "channels/t1/messages", """{"message":"Missing Access","code":50001}""", HttpStatusCode.Forbidden);
        var denied = await Assert.ThrowsExactlyAsync<MessagingDeliveryException>(() => provider.SendAsync(thread, new OutboundMessage(MessageKind.Info, "x"), default));
        Assert.IsTrue(denied.Permanent);
        StringAssert.Contains(denied.Message, "Missing Access");

        _discord.Respond(HttpMethod.Post, "channels/t1/messages", "", HttpStatusCode.BadGateway);
        Assert.IsFalse((await Assert.ThrowsExactlyAsync<MessagingDeliveryException>(() => provider.SendAsync(thread, new OutboundMessage(MessageKind.Info, "x"), default))).Permanent);
    }

    [TestMethod]
    public async Task Editing_and_health_use_the_right_endpoints()
    {
        _discord.Respond(new HttpMethod("PATCH"), "channels/t1/messages/m9", """{"id":"m9"}""");
        _discord.Respond(HttpMethod.Get, "users/@me", """{"id":"1","username":"agentd-bot"}""");
        var provider = Provider();

        await provider.EditAsync(new MessageRef(new ConversationRef(provider.Key, "t1", null), "m9"), new OutboundMessage(MessageKind.Progress, "step 2"), default);
        var health = await provider.CheckHealthAsync(default);

        Assert.AreEqual("step 2", _discord.Body(0)["content"]!.GetValue<string>());
        Assert.AreEqual(new ProviderHealth(true, "connected as agentd-bot"), health);
    }

    [TestMethod]
    public void Validation_requires_token_and_numeric_ids_only_when_enabled()
    {
        var validator = new DiscordOptionsValidator();

        Assert.IsTrue(validator.Validate(null, new DiscordOptions()).Succeeded, "disabled: nothing required");
        var result = validator.Validate(null, new DiscordOptions { Enabled = true, GuildId = "abc" });
        StringAssert.Contains(result.FailureMessage, "no bot token");
        StringAssert.Contains(result.FailureMessage, "GuildId");
        StringAssert.Contains(result.FailureMessage, "ChannelId");
        Assert.IsTrue(validator.Validate(null, new DiscordOptions { Enabled = true, BotToken = "t", GuildId = "770517485715193877", ChannelId = "1555955347544608809" }).Succeeded);
    }

    public void Dispose() => _discord.Dispose();

    private DiscordMessagingProvider Provider()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DiscordOptions { Enabled = true, BotToken = "test-token", GuildId = "777", ChannelId = "555" });
        var http = new HttpClient(new DiscordAuthHandler(options) { InnerHandler = _discord }) { BaseAddress = new Uri("https://discord.test/api/v10/") };
        return new DiscordMessagingProvider(new DiscordRest(() => http), options);
    }

    /// <summary>Records requests and answers with canned responses (latest match wins).</summary>
    internal sealed class FakeDiscord : HttpMessageHandler
    {
        private readonly List<(HttpMethod Method, string Path, string Json, HttpStatusCode Status)> _routes = [];

        public List<(HttpMethod Method, string Path, string Body, string ContentType, string Auth)> Requests { get; } = [];

        public void Respond(HttpMethod method, string path, string json, HttpStatusCode status = HttpStatusCode.OK) =>
            _routes.Add((method, path, json, status));

        public JsonNode Body(int index) => JsonNode.Parse(Requests[index].Body)!;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath.Replace("/api/v10/", "", StringComparison.Ordinal);
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, path, body, request.Content?.Headers.ContentType?.ToString() ?? "", request.Headers.Authorization?.ToString() ?? ""));
            var route = _routes.LastOrDefault(r => r.Method == request.Method && r.Path == path);
            return route.Path is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message":"Unknown"}""") }
                : new HttpResponseMessage(route.Status) { Content = new StringContent(route.Json, Encoding.UTF8, "application/json") };
        }
    }
}
