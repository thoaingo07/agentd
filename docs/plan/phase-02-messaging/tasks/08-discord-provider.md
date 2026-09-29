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

## Done when
- [ ] Manual check in a test guild: thread created, buttons work, slash commands respond, and
  `@everyone` from the agent pings nobody.
- [ ] The contract suite is green for Discord.
