# T2.2 — Messaging ports and neutral message model

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.1 | S | Agentd.Application |

## Goal
Define the provider-agnostic contract that every chat platform implements, and the neutral inbound
and outbound message types. After this task, no Application code mentions Discord or Telegram.

## Files
- `src/Agentd.Application/Messaging/IMessagingProvider.cs` — create.
- `src/Agentd.Application/Messaging/IMessagingProviderRegistry.cs` — create.
- `src/Agentd.Application/Messaging/Models/*.cs` — create: `OutboundMessage`, `InboundMessage`,
  `InboundCommand`, `MessageOption`, `Attachment`, `MessagingCapabilities`, `ConversationSpec`,
  `ConversationRef`, `MessageRef`, `ProviderHealth`, `MessageKind`.
- `src/Agentd.Application/Messaging/IInboundMessageSink.cs` — create: what provider listeners call.

## Implementation
1. Port, as designed in messaging-providers.md §3:
   ```csharp
   public interface IMessagingProvider
   {
       ProviderKey Key { get; }
       MessagingCapabilities Capabilities { get; }
       Task<ConversationRef> OpenConversationAsync(ConversationSpec spec, CancellationToken ct);
       Task<MessageRef> SendAsync(ConversationRef conversation, OutboundMessage message, CancellationToken ct);
       Task EditAsync(MessageRef message, OutboundMessage replacement, CancellationToken ct);
       Task CloseConversationAsync(ConversationRef conversation, string reason, CancellationToken ct);
       Uri? GetLink(ConversationRef conversation);
       Task<ProviderHealth> CheckHealthAsync(CancellationToken ct);
   }
   ```
2. `MessagingCapabilities(int MaxMessageLength, bool SupportsConversations, bool SupportsOptions,
   bool SupportsAttachments, bool SupportsEditing)`.
3. `OutboundMessage(MessageKind Kind, string Markdown, IReadOnlyList<MessageOption>? Options,
   IReadOnlyList<Attachment>? Attachments)`. The Markdown is a documented CommonMark subset:
   paragraphs, `**bold**`, `*italic*`, inline code, fenced code blocks and links. Nothing else.
4. `InboundMessage(ProviderKey Provider, string ExternalMessageId, string ExternalConversationId,
   string ExternalUserId, string UserDisplayName, string? Text, InboundCommand? Command,
   string? SelectedOptionId, DateTimeOffset SentAt)`.
5. `InboundCommand(string Name, IReadOnlyList<string> Args)`. Providers normalize platform syntax
   (Discord slash commands, `/status@bot`) into this neutral form. Unknown names are passed through
   and rejected by the Application layer.
6. `IInboundMessageSink.HandleAsync(InboundMessage, CancellationToken)` is implemented by the
   `HandleInboundMessage` use case (T2.6). Providers depend only on this interface.
7. `MessageOption(string Id, string Label)`. The `Id` is at most 32 ASCII characters, so it fits
   Telegram's 64-byte `callback_data` once prefixed.

## Tests
- Model invariants: `MessageOption.Id` length and charset; `OutboundMessage` requires non-empty
  Markdown or at least one attachment.
- Architecture test: `Agentd.Application.Messaging` has no reference to `Discord*` or `Telegram*`.

## Done when
- [ ] Ports and models compile, with XML docs on every public member.
- [ ] The architecture test for provider independence is green.
