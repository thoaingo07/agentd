# Phase 2 — Messaging: Discord + Telegram (provider pattern)

**Goal:** the agent talks to the developer in chat. Each job gets a Discord thread or Telegram forum
topic. `ask_developer` posts a question, and the reply **resumes the Claude session**. Building
**two** providers now proves that the abstraction works.

Design refs: [messaging-providers.md](../architect/messaging-providers.md) ·
[Discord reference](../architect/references/discord.md) · [Telegram reference](../architect/references/telegram.md)

---

## Scope

**In**

- **Domain:** a `WaitingForHuman` state and transitions; the `Conversation` entity; `ProviderKey`.
- **Application:**
  - the `IMessagingProvider` / `IMessagingProviderRegistry` ports;
  - `MessagingService` (routing, fan-out, chunking, progress coalescing);
  - `HandleInboundMessage` (dedupe → authorize → route → command or message);
  - `AskDeveloper`, `ReportProgress`, `SubmitDeveloperMessage`;
  - the neutral commands `status`, `cancel`, `retry`, `logs`, `list`, `run`.
- **Transactional outbox** (`outbound_messages`) + `MessagingDispatcherWorker`, and `inbound_messages`
  for idempotency.
- **User directory v0:** config-seeded `users` and `user_identities` tables (chat identities only),
  used as the chat allowlist. Roles are stored but not enforced until Phase 5.
- **`Infrastructure.Messaging.Discord`:** gateway listener, threads, slash commands, buttons,
  markdown renderer with escaping (mentions neutralized).
- **`Infrastructure.Messaging.Telegram`:** long polling, forum topics, bot commands, inline
  keyboards, HTML renderer with escaping, and reply-chain fallback mode.
- **MCP:** `ask_developer(question, options?)` and `report_progress` (edits the status message in place).
- **Mirroring** between providers when a job is on more than one.
- The shared **provider contract test suite**.

**Out:** Web UI messages (Phase 3), gate buttons (Phase 4), role enforcement (Phase 5).

---

## Tasks

1. Domain + migration: `conversations`, `outbound_messages`, `inbound_messages`, `users`, `user_identities`.
2. Ports and neutral models (`OutboundMessage`, `InboundMessage`, `MessagingCapabilities`).
3. `MessagingService`:
   - open conversations when a job starts;
   - chunk to the provider's `MaxMessageLength`, never breaking a code block;
   - rate-limit per conversation.
4. Outbox dispatcher with backoff. A provider outage must not fail a job, and `/healthz` reports the
   provider as degraded.
5. `HandleInboundMessage` + command parser + reply routing ("the first answer wins"; later ones are
   queued as the next turn).
6. `ask_developer`:
   - the MCP tool returns "end your turn";
   - the runner sees the process exit;
   - the job moves to `WaitingForHuman`;
   - a reply calls `claude --resume`.
7. Discord provider + recorded fixtures.
8. Telegram provider + recorded fixtures, with long-polling offset persistence.
9. The contract test suite, run against both providers.
10. Configuration: `Messaging:*`, top-level `Users`, `chat:<provider>` tag routing.

## Exit criteria (the demo)

- A job whose work item is ambiguous:
  1. the agent calls `ask_developer` with options;
  2. buttons appear in both Discord and Telegram;
  3. clicking one in Telegram resumes the session;
  4. the answer is mirrored into the Discord thread;
  5. the PR link is posted to both.
- An unknown chat user's reply is ignored and logged.
- `/cancel` in a thread cancels the job, and `/status` shows the state, turns and elapsed time.
- With Discord blocked (network off), the job continues. Messages flush when it comes back.
- Malicious text (`@everyone`, `<b>`, markdown injection) is rendered inert on both platforms (a test).

## Risks / open questions for review

- **Do we build both providers now?** The recommendation is yes, to validate the abstraction. The
  alternative is Discord now and Telegram in Phase 10.
- **Default provider(s)** for new jobs?
- **Telegram mode:** long polling (default, no public URL). Is a webhook needed at all?
- **Discord library:** Discord.Net (planned) or NetCord?
