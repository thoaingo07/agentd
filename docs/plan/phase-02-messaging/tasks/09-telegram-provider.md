# T2.9 — Telegram provider

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.2, T2.5, T2.6 | L | Agentd.Infrastructure.Messaging.Telegram |

## Goal
Implement `IMessagingProvider` for Telegram:
- one **forum topic** per job in a supergroup;
- **inline keyboards** for options and **bot commands**;
- **long polling** with persisted offsets;
- an HTML renderer with strict escaping;
- a **reply-chain fallback** when Topics are disabled.

## Files
- `src/Agentd.Infrastructure.Messaging.Telegram/TelegramMessagingProvider.cs` — create.
- `src/Agentd.Infrastructure.Messaging.Telegram/TelegramPollingService.cs` — create: hosted long-polling loop.
- `src/Agentd.Infrastructure.Messaging.Telegram/TelegramRenderer.cs` — create.
- `src/Agentd.Infrastructure.Messaging.Telegram/ServiceCollectionExtensions.cs` — create: `AddMessagingTelegram(config)`.
- `src/Agentd.Infrastructure.Persistence/Messaging/ProviderCursorStore.cs` — create: `provider_cursors (provider, key, value, updated_at)`.
- Migration — add the `provider_cursors` table.
- `tests/Agentd.Infrastructure.Tests/Messaging/Telegram/*` — create.

## Implementation
Library: **Telegram.Bot**, with the version pinned. Verify every method name against the pinned
version, because the API surface changes between major versions.

1. **Capabilities:** `MaxMessageLength = 4096`, with conversations (when Topics are enabled),
   options, attachments and editing all true.
2. **Startup check:**
   - `getChat(ChatId)` confirms the chat is a supergroup and reports `is_forum`;
   - `getChatMember(bot)` confirms admin with `can_manage_topics`;
   - if Topics are off → **reply-chain mode**, with a warning in the logs and health status.
3. **OpenConversationAsync:**
   - **forum:** `createForumTopic(chat, "WI-<id> · <title>")` → `message_thread_id`, then post the
     starter message in it; `ExternalConversationId = "<chatId>:<threadId>"`;
   - **reply-chain:** post a root message; `ExternalConversationId = "<chatId>:r<rootMessageId>"`.
   - Link: `https://t.me/c/<chat id without -100>/<threadId>`.
4. **SendAsync:**
   - `sendMessage(chat, html, message_thread_id, parse_mode: HTML)`;
   - in reply-chain mode, use `reply_parameters` pointing at the root message;
   - options → `InlineKeyboardMarkup` with `callback_data = "o:<outboxId>:<optionId>"`, checked to
     be at most 64 bytes;
   - attachments → `sendDocument`.
5. **EditAsync:** `editMessageText` (and `editMessageReplyMarkup` to remove buttons after an
   answer). A "message is not modified" error is treated as success.
6. **Long polling** (`TelegramPollingService`):
   - loop `getUpdates(offset, timeout: 50, allowed_updates: [message, callback_query])`;
   - after each **successfully handled** batch, persist `offset = last update_id + 1` in
     `provider_cursors`;
   - network errors → back off (1 s → 30 s);
   - a 409 Conflict (a webhook is set) → log an error and call `deleteWebhook` once when configured
     to do so.
7. **Inbound mapping:**
   - ignore bots and chats other than the configured one;
   - **forum:** the key is `"<chatId>:<message_thread_id>"`;
   - **reply-chain:** a reply to the root message, or to any bot message in the chain, → the key
     from the root message (keep a `message_id → root` map in memory, rebuilt from recent
     conversations);
   - commands `/status`, `/cancel`, `/retry`, `/logs`, `/list`, `/run <id>`: strip `@botname` and
     map to `InboundCommand`;
   - `callback_query` → `SelectedOptionId`, then **always** `answerCallbackQuery(id)`.
8. **Command registration:** `setMyCommands` with the group-chat scope at startup.
9. **Renderer** (neutral Markdown → Telegram HTML):
   - escape `&`, `<` and `>` in all text;
   - emit only `<b>`, `<i>`, `<code>`, `<pre><code class="language-x">`, `<a href>` (http(s) only)
     and `<blockquote>`;
   - never pass through raw HTML from the input.
10. **Rate limits:** on a 429, honour `parameters.retry_after`, reported to the dispatcher as
    transient with that delay.

## Tests
- Renderer golden files: `<b>` in agent text is escaped; `&amp;` handling; a code block with `<`;
  a `javascript:` link is dropped.
- Recorded update fixtures:
  - a forum message → correct key;
  - a reply-chain reply → the root key;
  - `/status@agentd_bot` → the `status` command;
  - a `callback_query` → option + answered.
- Offset persistence: simulated crash mid-batch → the batch is re-fetched → dedupe prevents
  double handling.
- Passes the contract suite (T2.10).

## Done when
- [ ] Manual check in a test supergroup with Topics: topic created, buttons work, commands work.
- [ ] With Topics disabled, reply-chain mode routes replies correctly.
- [ ] The contract suite is green for Telegram.
