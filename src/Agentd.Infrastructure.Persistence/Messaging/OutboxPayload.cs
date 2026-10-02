using System.Text.Json;
using Agentd.Application.Messaging;

namespace Agentd.Infrastructure.Persistence.Messaging;

/// <summary>JSON for <c>outbound_messages.payload</c> and the <c>outbox_enqueue</c> argument.</summary>
public static class OutboxPayload
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);

    /// <summary>The <c>p_messages</c> argument of <c>agentd.outbox_enqueue</c>.</summary>
    public static string EnqueueJson(IReadOnlyList<OutboxMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return JsonSerializer.Serialize(
            messages.Select(m => new
            {
                kind = m.Message.Kind.ToString(),
                payload = ToDto(m),
                only = m.Options?.OnlyProviders?.Select(p => p.Value),
                except = m.Options?.ExceptProviders?.Select(p => p.Value),
                replace = m.Options?.ReplaceStatusMessage ?? false,
            }),
            s_json);
    }

    /// <summary>Reads a stored payload back (the dispatcher).</summary>
    public static (OutboundMessage Message, bool ReplaceStatusMessage) Read(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, s_json) ?? throw new JsonException("Empty outbox payload.");
        var message = new OutboundMessage(
            dto.Kind,
            dto.Markdown,
            dto.Options?.Select(o => new MessageOption(o.Id, o.Label)).ToList(),
            dto.Attachments?.Select(a => new Attachment(a.FileName, a.ContentType, a.Content)).ToList());
        return (message, dto.ReplaceStatusMessage);
    }

    private static Dto ToDto(OutboxMessage m) => new(
        m.Message.Kind,
        m.Message.Markdown,
        m.Message.Options?.Select(o => new OptionDto(o.Id, o.Label)).ToList(),
        m.Message.Attachments?.Select(a => new AttachmentDto(a.FileName, a.ContentType, a.Content.ToArray())).ToList(),
        m.Options?.ReplaceStatusMessage ?? false);

    private sealed record Dto(MessageKind Kind, string Markdown, List<OptionDto>? Options, List<AttachmentDto>? Attachments, bool ReplaceStatusMessage);

    private sealed record OptionDto(string Id, string Label);

#pragma warning disable CA1819 // a DTO; System.Text.Json writes byte[] as base64
    private sealed record AttachmentDto(string FileName, string ContentType, byte[] Content);
#pragma warning restore CA1819
}
