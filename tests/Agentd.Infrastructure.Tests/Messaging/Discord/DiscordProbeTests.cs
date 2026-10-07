using System.Net;
using Agentd.Application.Setup;
using Agentd.Infrastructure.Messaging.Discord;
using Agentd.Infrastructure.Tests.AzureDevOps;

namespace Agentd.Infrastructure.Tests.Messaging.Discord;

[TestClass]
public sealed class DiscordProbeTests : IDisposable
{
    private const string Guild = "770517485715193877";
    private const string Channel = "1555955347544608809";
    private readonly FakeAdo _discord = new();

    [TestMethod]
    public async Task A_working_bot_posts_a_test_message_in_the_channel()
    {
        _discord.On(HttpMethod.Get, "/api/v10/users/@me", HttpStatusCode.OK, """{"username":"agentd"}""")
            .On(HttpMethod.Get, $"/api/v10/channels/{Channel}", HttpStatusCode.OK, $$"""{"guild_id":"{{Guild}}","name":"agentd"}""")
            .On(HttpMethod.Post, $"/api/v10/channels/{Channel}/messages", HttpStatusCode.OK, "{}");

        var check = await Probe().TestAsync(new ChatConnection("bot-token", Guild, Channel), CancellationToken.None);

        Assert.IsTrue(check.Ok, check.Message);
        Assert.AreEqual("agentd posted a test message in #agentd.", check.Message);
        Assert.IsTrue(_discord.Requests.All(r => r.Auth == "Bot bot-token"));
        Assert.Contains("agentd setup", _discord.Requests.Last().Body!);
    }

    [TestMethod]
    public async Task A_rejected_token_says_where_to_get_a_new_one()
    {
        _discord.On(HttpMethod.Get, "/api/v10/users/@me", HttpStatusCode.Unauthorized);

        var check = await Probe().TestAsync(new ChatConnection("bad", Guild, Channel), CancellationToken.None);

        Assert.IsFalse(check.Ok);
        Assert.Contains("Reset Token", check.Fix!);
    }

    [TestMethod]
    [DataRow(HttpStatusCode.Forbidden, null, "invite the bot")]
    [DataRow(HttpStatusCode.OK, "999999999999999999", "copy both IDs again")]
    public async Task A_channel_the_bot_cant_use_fails_with_the_fix(HttpStatusCode status, string? guild, string fix)
    {
        _discord.On(HttpMethod.Get, "/api/v10/users/@me", HttpStatusCode.OK, """{"username":"agentd"}""")
            .On(HttpMethod.Get, $"/api/v10/channels/{Channel}", status, guild is null ? null : $$"""{"guild_id":"{{guild}}"}""");

        var check = await Probe().TestAsync(new ChatConnection("bot-token", Guild, Channel), CancellationToken.None);

        Assert.IsFalse(check.Ok);
        Assert.Contains(fix, check.Fix!);
        Assert.IsFalse(_discord.Requests.Any(r => r.Method == HttpMethod.Post), "no message without access");
    }

    public void Dispose() => _discord.Dispose();

    private DiscordProbe Probe() => new(Microsoft.Extensions.Options.Options.Create(new DiscordOptions()), () => _discord);
}
