# Phase 2 — Tasks

Detailed implementation tasks for [Phase 2 — Messaging](../README.md), in build order.

| ID | Task | Depends on | Size | Status |
|---|---|---|---|---|
| T2.1 | [Domain model and database schema for messaging](01-domain-and-schema.md) | Phase 1 | M | ☐ |
| T2.2 | [Messaging ports and neutral message model](02-ports-and-neutral-models.md) | T2.1 | S | ☐ |
| T2.3 | [User directory v0 and messaging configuration](03-users-directory-and-config.md) | T2.1 | S | ☐ |
| T2.4 | [MessagingService (outbound routing, chunking, progress)](04-messaging-service.md) | T2.2, T2.3 | M | ☐ |
| T2.5 | [Outbox dispatcher, retries and provider health](05-outbox-dispatcher.md) | T2.4 | M | ☐ |
| T2.6 | [HandleInboundMessage, commands and mirroring](06-inbound-handling-and-commands.md) | T2.2, T2.3, T2.4 | M | ☐ |
| T2.7 | [`ask_developer`, progress and resuming the Claude session](07-ask-developer-and-resume.md) | T2.4, T2.6, Phase 1 | M | ☐ |
| T2.8 | [Discord provider](08-discord-provider.md) | T2.2, T2.5, T2.6 | L | ☐ |
| T2.9 | [Telegram provider](09-telegram-provider.md) | T2.2, T2.5, T2.6 | L | ☐ |
| T2.10 | [Provider contract test suite](10-provider-contract-tests.md) | T2.8, T2.9 | M | ☐ |
| T2.11 | [End-to-end verification and phase demo](11-end-to-end-verification.md) | T2.1 – T2.10 | S | ☐ |
