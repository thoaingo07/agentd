using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Agentd.Application.Messaging;
using Agentd.Domain.Messaging;
using Microsoft.Extensions.Options;

namespace Agentd.Infrastructure.Messaging.Discord;

/// <summary>
/// Discord over REST: one public thread per job in the configured channel, plain messages (mentions
/// always disabled), edits for the live status message, files for attachments. Options are a
/// numbered list; the last options sent to each thread are remembered so a reply like "2" maps back.
/// </summary>
public sealed class DiscordMessagingProvider(DiscordRest rest, IOptions<DiscordOptions> options) : IMessagingProvider
{
    public const string Name = "discord";
    public const int ThreadNameLimit = 100;
    public const int AutoArchiveMinutes = 10080;   // 7 days

    private readonly ConcurrentDictionary<string, IReadOnlyList<MessageOption>> _lastOptions = new();

    public ProviderKey Key { get; } = ProviderKey.From(Name);

    public MessagingCapabilities Capabilities { get; } = new(
        MaxMessageLength: 2000, SupportsConversations: true, SupportsOptions: false, SupportsAttachments: true, SupportsEditing: true);

    /// <summary>The options most recently offered in <paramref name="threadId"/> (for numbered replies).</summary>
    public IReadOnlyList<MessageOption>? LastOptions(string threadId) => _lastOptions.TryGetValue(threadId, out var o) ? o : null;

    public async Task<ConversationRef> OpenConversationAsync(ConversationSpec spec, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var o = options.Value;
        var starter = await PostAsync(o.ChannelId, spec.Opening, cancellationToken).ConfigureAwait(false);
        var thread = await rest.PostAsync($"channels/{o.ChannelId}/messages/{starter}/threads",
            new JsonObject { ["name"] = ThreadName(spec), ["auto_archive_duration"] = AutoArchiveMinutes },
            cancellationToken).ConfigureAwait(false);
        return new ConversationRef(Key, Id(thread), o.GuildId);
    }

    public async Task<MessageRef> SendAsync(ConversationRef conversation, OutboundMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(message);
        var id = await PostAsync(conversation.ExternalConversationId, message, cancellationToken).ConfigureAwait(false);
        if (message.Options is { Count: > 0 } offered)
        {
            _lastOptions[conversation.ExternalConversationId] = offered;
        }

        return new MessageRef(conversation, id);
    }

    public async Task EditAsync(MessageRef message, OutboundMessage replacement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(replacement);
        await rest.PatchAsync($"channels/{message.Conversation.ExternalConversationId}/messages/{message.ExternalMessageId}",
            Payload(replacement), cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseConversationAsync(ConversationRef conversation, string reason, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        await PostAsync(conversation.ExternalConversationId, new OutboundMessage(MessageKind.Info, reason), cancellationToken).ConfigureAwait(false);
        await rest.PatchAsync($"channels/{conversation.ExternalConversationId}", new JsonObject { ["archived"] = true }, cancellationToken).ConfigureAwait(false);
    }

    public Uri? GetLink(ConversationRef conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        return new Uri($"https://discord.com/channels/{options.Value.GuildId}/{conversation.ExternalConversationId}");
    }

    public async Task<ProviderHealth> CheckHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            var me = await rest.GetAsync("users/@me", cancellationToken).ConfigureAwait(false);
            return new ProviderHealth(true, $"connected as {me?["username"]?.GetValue<string>() ?? "bot"}");
        }
        catch (MessagingDeliveryException ex)
        {
            return new ProviderHealth(false, ex.Message);
        }
    }

    /// <summary><c>WI-1234 · Title</c>, at most 100 characters.</summary>
    public static string ThreadName(ConversationSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var name = $"WI-{spec.WorkItemId} · {spec.Title.ReplaceLineEndings(" ").Trim()}";
        return name.Length <= ThreadNameLimit ? name : name[..(ThreadNameLimit - 1)] + "…";
    }

    /// <summary>Message JSON; <c>allowed_mentions</c> is always empty so nothing ever pings.</summary>
    public static JsonObject Payload(OutboundMessage message) => new()
    {
        ["content"] = DiscordRenderer.Render(message),
        ["allowed_mentions"] = new JsonObject { ["parse"] = new JsonArray() },
    };

    private async Task<string> PostAsync(string channelId, OutboundMessage message, CancellationToken ct)
    {
        var payload = Payload(message);
        var created = message.Attachments is { Count: > 0 } files
            ? await rest.PostWithFilesAsync($"channels/{channelId}/messages", payload, files, ct).ConfigureAwait(false)
            : await rest.PostAsync($"channels/{channelId}/messages", payload, ct).ConfigureAwait(false);
        return Id(created);
    }

    private static string Id(JsonNode? node) =>
        node?["id"]?.GetValue<string>() ?? throw new MessagingDeliveryException("Discord response had no id.", permanent: false);
}
