# Phase 2 — Messaging: Discord + Telegram (provider pattern)

**Goal:** the agent talks to the developer in chat. Each job gets a Discord thread or Telegram forum
topic. `ask_developer` posts a question, and the reply **resumes the Claude session**. Building
**two** providers now proves that the abstraction works.

Design refs: [messaging-providers.md](../../architect/messaging-providers.md) ·
[Discord reference](../../architect/references/discord.md) · [Telegram reference](../../architect/references/telegram.md)

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

Detailed tasks: [tasks/README.md](tasks/README.md)

| ID | Task | Depends on | Size | Status |
|---|---|---|---|---|
| T2.1 | [Domain model and database schema for messaging](tasks/01-domain-and-schema.md) | Phase 1 | M | ☑ |
| T2.2 | [Messaging ports and neutral message model](tasks/02-ports-and-neutral-models.md) | T2.1 | S | ☑ |
| T2.3 | [User directory v0 and messaging configuration](tasks/03-users-directory-and-config.md) | T2.1 | S | ☑ |
| T2.4 | [MessagingService (outbound routing, chunking, progress)](tasks/04-messaging-service.md) | T2.2, T2.3 | M | ☑ |
| T2.5 | [Outbox dispatcher, retries and provider health](tasks/05-outbox-dispatcher.md) | T2.4 | M | ☐ |
| T2.6 | [HandleInboundMessage, commands and mirroring](tasks/06-inbound-handling-and-commands.md) | T2.2, T2.3, T2.4 | M | ☐ |
| T2.7 | [`ask_developer`, progress and resuming the Claude session](tasks/07-ask-developer-and-resume.md) | T2.4, T2.6, Phase 1 | M | ☐ |
| T2.8 | [Discord provider](tasks/08-discord-provider.md) | T2.2, T2.5, T2.6 | L | ☐ |
| T2.9 | [Telegram provider](tasks/09-telegram-provider.md) — *deferred* | T2.2, T2.5, T2.6 | L | ⏸ |
| T2.10 | [Provider contract test suite](tasks/10-provider-contract-tests.md) | T2.8, T2.9 | M | ☐ |
| T2.11 | [End-to-end verification and phase demo](tasks/11-end-to-end-verification.md) | T2.1 – T2.10 | S | ☐ |

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

## Decisions (2026-10-01)

- **Discord only** in Phase 2. Telegram (T2.9) is deferred to a later phase; the provider port and
  the contract suite (T2.10) stay provider-neutral so it can be added without Application changes.
- **Discord library: NetCord.**
- **Default providers:** every enabled provider gets a conversation (`DefaultProviders` empty = all
  enabled); `chat:<provider>` tags override.
