# T2.8 — Discord provider

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.2, T2.5, T2.6 | L | Agentd.Infrastructure.Messaging.Discord |

## Goal
Implement `IMessagingProvider` for Discord:
- one **thread** per job in the configured channel;
- **buttons** for options;
- **slash commands** for the neutral commands;
- a renderer that makes agent- and user-supplied text inert (no mass mentions, no markup injection).

## Files
- `src/Agentd.Infrastructure.Messaging.Discord/DiscordMessagingProvider.cs` — create.
- `src/Agentd.Infrastructure.Messaging.Discord/DiscordGatewayService.cs` — create: hosted `DiscordSocketClient`.
- `src/Agentd.Infrastructure.Messaging.Discord/DiscordRenderer.cs` — create.
- `src/Agentd.Infrastructure.Messaging.Discord/SlashCommands.cs` — create.
- `src/Agentd.Infrastructure.Messaging.Discord/ServiceCollectionExtensions.cs` — create: `AddMessagingDiscord(config)`.
- `tests/Agentd.Infrastructure.Tests/Messaging/Discord/*` — create: fixtures + tests.

## Implementation
Library: **Discord.Net**, with the version pinned in `Directory.Packages.props`. Verify every
signature below against the pinned version.

1. **Client:** `DiscordSocketClient` with `GatewayIntents.Guilds | GuildMessages | MessageContent`,
   started in `DiscordGatewayService.StartAsync` and logged in with the bot token. Reconnects are
   handled by the library; reconnect and disconnect events are logged as health signals.
2. **Capabilities:** `MaxMessageLength = 2000`, conversations, options, attachments and editing
   all true.
3. **OpenConversationAsync:** post a starter message in the channel, then create a public thread
   from it. The name is `WI-<id> · <title>`, truncated to 100 characters, with auto-archive at 7
   days (clamped to what the guild allows). Returns the thread ID and the link
   `https://discord.com/channels/{guild}/{thread}`.
4. **SendAsync:**
   - render (step 7), then send in the thread;
   - `Options` become a component row of buttons, with `custom_id = "agentd:opt:<outboxId>:<optionId>"`
     (at most 5 per row);
   - attachments are sent as files;
   - **always** pass `AllowedMentions.None`.
5. **EditAsync:** modify the message content/components (used for the status message and for
   showing the answered question).
6. **Inbound:**
   - **`MessageReceived`:** ignore bots (including ourselves) and messages outside the configured
     guild. For thread messages, build an `InboundMessage` with `ExternalConversationId = thread.Id`.
   - **`SlashCommandExecuted`:** map `/agentd status|cancel|retry|logs|list|run id:<n>` to an
     `InboundCommand`. Acknowledge within 3 seconds with an ephemeral "Working…", then follow up.
   - **`ButtonExecuted`:** parse the `custom_id` into `SelectedOptionId`, then acknowledge
     (defer update).
   - Then call `IInboundMessageSink.HandleAsync`.
7. **Renderer** (neutral Markdown → Discord markdown):
   - escape `* _ ~ \` | > #` and `[`/`]` in text runs;
   - neutralize `@everyone` and `@here` by inserting a zero-width space after the `@`, and render
     `<@id>` / `<#id>` / `<@&id>` literally;
   - code blocks keep their content, with any embedded ``` sequences neutralized;
   - links render as `[text](url)` only for `http(s)` URLs.
8. **Backlog after downtime:** on `Ready`, for each open conversation, fetch messages after the
   last processed ID (up to 100) and feed them through the sink. Dedupe (T2.6) makes this safe.
9. **Slash command registration:** per guild at startup, idempotently (upsert).

## Tests
- Renderer: `@everyone`, `<@123>`, `**x**`, a nested code fence, `[a](javascript:…)` are all inert.
  Golden-file tests.
- Recorded fixtures (serialized gateway payloads → inbound mapping): a thread message, a bot
  message (ignored), a slash command, a button press.
- Fake REST handler tests: thread naming and truncation, button `custom_id` format, and that
  `AllowedMentions` is always none.
- Passes the contract suite (T2.10).

## As built (decided 2026-10-03: REST + polling, no library)
NetCord is beta-only and the rule here is stable packages; Discord.Net works but adds a dependency.
Plain REST covers everything outbound. Receiving needs either the Gateway (a WebSocket protocol to own)
or polling. **We chose REST with polling:** no dependency and no public URL, at the cost of buttons and
slash commands.

Split into two PRs:

1. **Outbound** (this one), in project `Agentd.Infrastructure.Messaging.Discord`:
   - **`DiscordRest`:** creates its `HttpClient` from the factory on each call, and `DiscordAuthHandler`
     adds `Bot <token>` and the required User-Agent. A 429 becomes a transient
     `MessagingDeliveryException` carrying `retry_after`; 5xx and network errors are transient; other
     4xx (Missing Access, Unknown Channel) are permanent.
   - **`DiscordMessagingProvider`:**
     - a starter message in the channel, then a public thread `WI-<id> · <title>` (≤ 100 characters,
       auto-archive after 7 days);
     - messages always carry `allowed_mentions: {parse: []}`;
     - edits use `PATCH`, files go as multipart `payload_json` + `files[n]`, closing posts the reason
       and archives the thread, and health is `GET users/@me`;
     - `SupportsOptions = false`: options render as a numbered list, and the last options per thread
       are remembered for the poller.
   - **`DiscordRenderer`:** `@everyone`/`@here` and `<@id>`/`<#id>`/`<@&id>` become inert (a zero-width
     space), only http(s) links render, and a leading `#`/`-#` is escaped. Code blocks are untouched.
     Markdown emphasis passes through on purpose (it's harmless); `allowed_mentions` is the guarantee.
   - **`DiscordOptions`** (`Agentd:Messaging:Providers:Discord`: `Enabled`, `BotToken` from secrets
     only, `GuildId`, `ChannelId`, `PollInterval`, `CommandPrefix`), with a validator that fails startup
     when Discord is enabled without a token or numeric ids.
2. **Inbound:**
   - **`DiscordPoller`** is a hosted service that runs only when enabled. Every `PollInterval` (3 s) it
     reads `GET channels/{id}/messages?after={last}` for the parent channel (commands only) and for
     each open job thread (the new `conversation_list_open(provider)` routine and
     `IConversationStore.ListOpenAsync`). It hands messages oldest first to `IInboundMessageSink` in a
     scope.
   - **Cursor:** in memory, advanced only after a message is handled, so a failure retries it.
     - At startup each thread replays its last 50 messages, so replies sent while agentd was down are
       picked up; T2.6 dedupe makes this safe.
     - The parent channel starts from its newest message, so old commands never run.
     - A thread that fails (deleted, no access) doesn't stop the others.
   - **`DiscordInbound.Map`:**
     - bots (including agentd itself) and empty messages are ignored;
     - `!name args` becomes a neutral command;
     - a bare number (`2` or `2.`) that answers the thread's last options becomes that option, with its
       label as the text.
     - The option memory is in-process: after a restart, a number is delivered as plain text.
   - **Development config:** the sandbox guild, channel and user ids, with `Enabled: false`. To try it,
     set the bot token (user-secrets), then turn `Enabled` on.

## Done when
- [ ] Manual check in a test guild: thread created, buttons work, slash commands respond, and
  `@everyone` from the agent pings nobody.
- [ ] The contract suite is green for Discord.
