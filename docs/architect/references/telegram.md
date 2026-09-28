# Telegram reference

The Telegram messaging provider is built on the Bot API (`https://api.telegram.org/bot<token>/<method>`)
with the **Telegram.Bot** NuGet package.

## Bot setup

1. Talk to **@BotFather** → `/newbot` → note the token and store it as
   `Agentd__Messaging__Providers__Telegram__BotToken`.
2. **Privacy mode:** with `/setprivacy` → *Disable*, the bot receives every group message, not only
   commands and replies. Alternatively, making the bot an admin also lets it see all messages.
3. Create a **supergroup**, enable **Topics** (Group settings → Topics), and add the bot as an
   **admin** with the *Manage Topics* right.
4. Get the chat ID (for example `-1001234567890`) from `getUpdates` after sending a message in the
   group, then set `Messaging:Providers:Telegram:ChatId`.

## Receiving updates

- **Long polling (default):** `getUpdates(offset, timeout: 50, allowed_updates: ["message", "callback_query"])`.
  No public URL is needed, which suits a localhost daemon. Persist the last `update_id + 1` as the
  offset so a restart does not reprocess updates. Inbound dedupe is still enforced by `inbound_messages`.
- **Webhook (optional):** `setWebhook(url, secret_token)`. Verify the
  `X-Telegram-Bot-Api-Secret-Token` header on every request. This requires public HTTPS through a
  tunnel or proxy. The two modes are mutually exclusive, so a webhook disables `getUpdates`.

## One topic per job

| Action | Method |
|---|---|
| Open | `createForumTopic(chat_id, name: "WI-1234 · Fix login", icon_color)` → `message_thread_id` |
| Post | `sendMessage(chat_id, text, message_thread_id, parse_mode: "HTML")` |
| Live progress | `editMessageText(chat_id, message_id, text)` |
| File | `sendDocument(chat_id, document, message_thread_id)` |
| Close | `closeForumTopic(chat_id, message_thread_id)` (`reopenForumTopic` on retry) |

Inbound routing key: `"{chat_id}:{message_thread_id}"`.

Link format for private supergroups: `https://t.me/c/{chat_id without -100 prefix}/{message_thread_id}`.

## Buttons (ask_developer options)

- Send the question with `reply_markup: InlineKeyboardMarkup` of `InlineKeyboardButton(text, callback_data)`.
  `callback_data` is at most 64 bytes, so use a short option ID and never raw text.
- A button press arrives as a `callback_query`. Always call `answerCallbackQuery(id)` to clear the
  client spinner, then edit the question message to show the chosen answer.

## Commands

Register them with `setMyCommands(commands, scope: BotCommandScopeChat(chat_id))`:
`/status`, `/cancel`, `/retry`, `/logs`, `/list`, `/run <id>`.
In groups, commands may arrive as `/status@agentd_bot`, so strip the `@botname` suffix.

## Limits and formatting

- Text messages can be at most **4096** characters, and captions at most 1024.
- Bots can upload files of up to **50 MB**.
- Rate limits: about 1 message per second per chat, and about 20 messages per minute per group. On a
  `429`, honor `retry_after`.
- Use `parse_mode: "HTML"`. Escape `<`, `>` and `&` in all dynamic text. The allowed tags are
  `b i u s code pre a blockquote`.

## Links

- Bot API: https://core.telegram.org/bots/api
- Forum topics: https://core.telegram.org/bots/api#createforumtopic
- Telegram.Bot for .NET: https://telegrambots.github.io/book/
