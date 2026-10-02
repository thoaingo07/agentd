# T2.6 — HandleInboundMessage, commands and mirroring

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.2, T2.3, T2.4 | M | Agentd.Application |

## Goal
One use case processes every message from every provider. It dedupes, authorizes against the user
directory, routes to a job, executes neutral commands, turns replies into developer messages, and
mirrors them to the job's other conversations.

## Files
- `src/Agentd.Application/Messaging/HandleInboundMessage.cs` — create: implements `IInboundMessageSink`.
- `src/Agentd.Application/Messaging/Commands/*.cs` — create: `StatusCommand`, `CancelCommand`,
  `RetryCommand`, `LogsCommand`, `ListCommand`, `RunCommand`.
- `src/Agentd.Application/Jobs/SubmitDeveloperMessage.cs` — create.
- `src/Agentd.Infrastructure.Persistence/Messaging/InboundLog.cs` — create.

## Implementation
1. **Dedupe:** `INSERT INTO inbound_messages ... ON CONFLICT DO NOTHING`. If nothing was inserted,
   the message was already processed, so return.
2. **Authorize:** `IUserDirectory.FindByIdentityAsync(provider, externalUserId)`. If there's no
   match or the user is inactive, record the outcome `ignored_unknown_user`, log it at Information
   level, and **send no reply** (so we don't confirm the bot to strangers). Roles are captured for
   Phase 5 but not checked yet.
3. **Route:**
   - look up the conversation by `(provider, external_conversation_id)` → the job;
   - if there is no conversation but the message is in the configured parent space, only the
     global commands `list` and `run <id>` are allowed;
   - otherwise ignore it.
4. **Commands** (neutral names; providers already normalized them):
   - `status` → state, phase (later), turns, elapsed time and cost → reply;
   - `cancel` → `CancelJob(by: user)`;
   - `retry` → `RetryJob` (only when Failed);
   - `logs` → the last 200 lines of the transcript as an attachment;
   - `list` → active jobs with links;
   - `run <workItemId>` → `ClaimWorkItem`;
   - unknown command → a help message.
5. **Option pressed** (`SelectedOptionId`) → `SubmitDeveloperMessage(text: option label, optionId)`.
   The question message is then edited to show "✅ <label> — <user>" where editing is supported.
6. **Plain text** → `SubmitDeveloperMessage(text)`:
   - job `WaitingForHuman` → `Job.ResumeWith(reply)` → schedule a resume turn (T2.7). **The first
     answer wins**: the transition happens once, under an optimistic concurrency check on the job
     row version.
   - job `Running` → append it to the pending messages and reply "Queued for the next turn".
   - job in a final state → reply with the state.
7. **Mirroring:** after a reply is accepted, enqueue to the job's **other** conversations
   `↩ <name> (<Provider>): <text>`, marked `Mirrored = true`. Mirrored copies are sent by the bot,
   and bot-authored inbound messages are dropped by the providers, so they never loop back.
8. Everything above runs in one unit of work: the job update, the outbox rows and the inbound log.

## Carried over from T2.5
- Retry opening a job's missing conversations (a target provider with no conversation row): when a
  developer message or command arrives, and periodically for running jobs.

## Tests
- Duplicate delivery of the same external message ID → processed once.
- Unknown user → ignored, no reply, logged.
- Two replies racing on a waiting job → exactly one resumes it and the other is queued (concurrency
  test).
- Each command's happy path + unknown command; `run` in a thread vs in the parent space.
- Mirroring: a reply in Telegram → one outbox row for Discord, none for Telegram.

## Done when
- [ ] Every inbound path is covered by Application tests with fake ports.
- [ ] Race test is green 100 times in a row.
