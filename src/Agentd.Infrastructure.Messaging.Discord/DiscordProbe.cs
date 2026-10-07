using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Agentd.Application.Setup;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Messaging.Discord;

/// <summary>The chat step's Test: a one-off client with the bot token being tested (it may not be saved yet).</summary>
/// <param name="options">For the API's base URL.</param>
/// <param name="primary">The transport (tests); default: sockets.</param>
public sealed class DiscordProbe(IOptions<DiscordOptions> options, Func<HttpMessageHandler>? primary = null) : IChatProbe
{
    public const string TestMessage = "✅ agentd setup: this channel works. agentd posts its jobs here, one thread per work item.";

    private const string Invite = "invite the bot to the server with View Channel, Send Messages, Create Public Threads, Send Messages in Threads and Read Message History, and turn on the Message Content intent";

    public async Task<StepCheck> TestAsync(ChatConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var http = new HttpClient(primary?.Invoke() ?? new SocketsHttpHandler(), disposeHandler: true)
        {
            BaseAddress = options.Value.ApiBaseUrl,
            Timeout = TimeSpan.FromSeconds(20),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bot", connection.BotToken);
        try
        {
            using var me = await http.GetAsync(new Uri("users/@me", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (me.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new StepCheck(false, "Discord rejected the bot token.", "copy the token again: Discord Developer Portal → your application → Bot → Reset Token");
            }

            me.EnsureSuccessStatusCode();
            var bot = (await me.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false))?["username"]?.GetValue<string>() ?? "the bot";

            using var channel = await http.GetAsync(new Uri($"channels/{connection.ChannelId}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            if (channel.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                return new StepCheck(false, $"{bot} can't see channel {connection.ChannelId}.", Invite);
            }

            channel.EnsureSuccessStatusCode();
            var info = await channel.Content.ReadFromJsonAsync<JsonNode>(cancellationToken).ConfigureAwait(false);
            if (info?["guild_id"]?.GetValue<string>() != connection.GuildId)
            {
                return new StepCheck(false, $"Channel {connection.ChannelId} isn't in server {connection.GuildId}.", "copy both IDs again (Discord: Settings → Advanced → Developer Mode, then right-click → Copy ID)");
            }

            using var post = await http.PostAsJsonAsync(new Uri($"channels/{connection.ChannelId}/messages", UriKind.Relative), new { content = TestMessage }, cancellationToken).ConfigureAwait(false);
            return post.IsSuccessStatusCode
                ? new StepCheck(true, $"{bot} posted a test message in #{info?["name"]?.GetValue<string>() ?? connection.ChannelId}.")
                : new StepCheck(false, $"{bot} can see the channel but can't post there ({(int)post.StatusCode}).", Invite);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new StepCheck(false, ex is TaskCanceledException ? "Discord didn't answer in time." : $"Discord unreachable: {ex.Message}", "check that this server can reach discord.com");
        }
    }
}
