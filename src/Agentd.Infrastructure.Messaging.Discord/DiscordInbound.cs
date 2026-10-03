using System.Globalization;
using System.Text.Json.Nodes;
using Agentd.Application.Messaging;
using Agentd.Domain.Messaging;

namespace Agentd.Infrastructure.Messaging.Discord;

/// <summary>Maps a Discord message object (REST) to the neutral <see cref="InboundMessage"/>.</summary>
public static class DiscordInbound
{
    /// <summary>
    /// Null for messages agentd ignores: bots (including itself) and messages without text.
    /// <c>!name args</c> becomes a command; a bare number answering the thread's last options becomes
    /// that option (its label as the text).
    /// </summary>
    public static InboundMessage? Map(JsonNode message, string conversationId, string commandPrefix, IReadOnlyList<MessageOption>? lastOptions)
    {
        ArgumentNullException.ThrowIfNull(message);
        var author = message["author"];
        var content = message["content"]?.GetValue<string>()?.Trim();
        if (author is null || author["bot"]?.GetValue<bool>() == true || string.IsNullOrEmpty(content))
        {
            return null;
        }

        InboundCommand? command = null;
        string? text = content;
        string? option = null;
        if (content.StartsWith(commandPrefix, StringComparison.Ordinal) && content.Length > commandPrefix.Length && char.IsLetter(content[commandPrefix.Length]))
        {
            var parts = content[commandPrefix.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            command = new InboundCommand(parts[0].ToLowerInvariant(), parts[1..]);
            text = null;
        }
        else if (lastOptions is { Count: > 0 } && int.TryParse(content.TrimEnd('.'), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= lastOptions.Count)
        {
            option = lastOptions[n - 1].Id;
            text = lastOptions[n - 1].Label;
        }

        return new InboundMessage(
            ProviderKey.From(DiscordMessagingProvider.Name),
            message["id"]!.GetValue<string>(),
            conversationId,
            author["id"]!.GetValue<string>(),
            author["global_name"]?.GetValue<string>() ?? author["username"]?.GetValue<string>() ?? "unknown",
            text,
            command,
            option,
            message["timestamp"] is { } ts ? DateTimeOffset.Parse(ts.GetValue<string>(), CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow);
    }

    /// <summary>Discord ids are snowflakes: numeric order is creation order.</summary>
    public static ulong Snowflake(JsonNode message) => ulong.Parse(message["id"]!.GetValue<string>(), CultureInfo.InvariantCulture);
}
