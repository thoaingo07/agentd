# T2.4 — MessagingService (outbound routing, chunking, progress)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.2, T2.3 | M | Agentd.Application |

## Goal
Implement the provider-agnostic outbound side once:
- open conversations when a job starts;
- turn job events into neutral messages;
- split long messages safely;
- coalesce progress updates;
- write everything to the outbox **in the same transaction** as the state change that caused it.

## Files
- `src/Agentd.Application/Messaging/MessagingService.cs` — create.
- `src/Agentd.Application/Messaging/MessageChunker.cs` — create.
- `src/Agentd.Application/Messaging/ProgressCoalescer.cs` — create.
- `src/Agentd.Application/Messaging/IOutbox.cs` — create (port); implemented in Persistence.
- `src/Agentd.Infrastructure.Persistence/Messaging/Outbox.cs` — create.
- Phase 1 use cases (`StartNextJob`, `PublishPullRequest`, `FailJob`) — modify: call `MessagingService`.

## Implementation
1. **Opening conversations:** on `JobStarted`, resolve the targets (T2.3). For each target, call
   `OpenConversationAsync` **directly**, not through the outbox, because the conversation ID is
   needed before anything else can be sent. On failure, record a `messaging.degraded` event and
   continue; the dispatcher retries the open (T2.5). The starter message includes the work item
   link and title.
2. **Enqueue API** used by the use cases:
   ```csharp
   Task EnqueueAsync(JobId job, OutboundMessage message, EnqueueOptions? options, CancellationToken ct);
   // options: OnlyProviders, ExceptProviders (mirroring), ReplaceStatusMessage (progress)
   ```
   Writes one `outbound_messages` row per target conversation, in the same database call (routine) and
   transaction as the calling use case (unit of work).
3. **Chunking** (`MessageChunker.Split(markdown, maxLength)`):
   - split on paragraph boundaries, then on lines;
   - never inside a fenced code block: if a code block alone exceeds the limit, close the fence,
     continue in the next part with the same language tag, and reopen it;
   - add a `(1/3)` suffix per part;
   - reserve headroom for rendering overhead: the budget is `maxLength - 64`, because escaping can
     make the rendered text longer.
   - Chunking happens **at dispatch time**, per provider capability, so the outbox stores the
     unsplit message.
4. **Long content:** a message over 8,000 characters, or any diff or log, becomes an attachment when
   `SupportsAttachments` is true; otherwise it becomes a truncated message with a Web UI link
   (Phase 3 fills in the URL).
5. **Progress coalescing:** `report_progress` calls `EnqueueAsync(... ReplaceStatusMessage: true)`.
   - With `SupportsEditing`, the dispatcher edits `conversations.status_message_id`, which is created
     on first use.
   - Otherwise, pending progress rows for the same conversation are **collapsed**: only the newest
     is kept, and at most one is sent per 10 seconds.
6. **Per-conversation rate limit:** a token bucket (default 1 message/second, burst 3), enforced by
   the dispatcher and not by this service.
7. **Message catalog:** starter, progress, question, answer mirrored, PR ready, failed and
   cancelled. Each is a small function returning an `OutboundMessage`, and all user- or
   agent-supplied text is passed through as data. Escaping is the renderer's job (T2.8 and T2.9).

## Tests
- Chunker: short text is unchanged; exactly the limit; a code block spanning two parts is
  re-fenced with its language; no part exceeds the budget (property test over random Markdown).
- Enqueue within a failing transaction leaves no outbox rows.
- Progress: 5 rapid updates → one pending row with editing; the latest text wins.
- Target resolution integrates the defaults and tag override.

## As built
Split into two PRs to keep each under 1000 lines:

1. **Message shaping** (this part):
   - `MessageChunker.Prepare(message, capabilities)`: text over 8,000 characters becomes a
     `message.md` attachment when supported; otherwise it is split. Buttons and attachments stay on
     the last part.
   - `Split(markdown, maxLength)`: paragraph, then line, then word boundaries; oversized code blocks
     are re-fenced with their language; the budget is `maxLength − 64` including the `(i/n)` marker.
     A seeded property test runs 300 random documents each at 2000 and 4096 characters.
   - `MessageCatalog`: the messages agentd posts.
   - `MessagingProviderRegistry`: the registered providers that are enabled, in configuration order.
2. **Outbox and `MessagingService`** (next PR): `outbound_messages` routines, opening conversations
   on job start, enqueuing from the job use cases in the same routine call as the state change, and
   collapsing progress.

## Done when
- [ ] Every job lifecycle event from Phase 1 produces the expected outbox rows.
- [x] Chunker property tests pass for both 2000 (Discord) and 4096 (Telegram) limits.
- [ ] No Application code references a concrete provider.
