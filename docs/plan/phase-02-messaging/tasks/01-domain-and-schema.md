# T2.1 — Domain model and database schema for messaging

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | Phase 1 (`Job` aggregate, persistence routines) | M | Agentd.Domain, Agentd.Infrastructure.Persistence |

## Goal
Add the domain concepts that messaging needs: the `WaitingForHuman` job state, a per-provider
`Conversation`, and a `ProviderKey` value object. Also add the five tables that back messaging:
conversations, the outbox, inbound idempotency, and the v0 user directory.

## Files
- `src/Agentd.Domain/Jobs/Job.cs` — modify: add the `WaitingForHuman` state, `AskDeveloper(question)` and `ResumeWith(reply)`.
- `src/Agentd.Domain/Jobs/JobState.cs` — modify: add `WaitingForHuman`.
- `src/Agentd.Domain/Messaging/ProviderKey.cs` — create: value object (lower-case `[a-z0-9-]+`).
- `src/Agentd.Domain/Messaging/Conversation.cs` — create: entity owned by `Job`.
- `src/Agentd.Domain/Messaging/Events.cs` — create: `DeveloperQuestionAsked`, `DeveloperReplied`, `ConversationOpened`.
- `src/Agentd.Domain/Users/User.cs`, `UserIdentity.cs` — create: directory v0 entities.
- `src/Agentd.Migrator/Migrations/{yyyyMMddNNNN}_messaging.up.sql` (+ `.down.sql`, line in `Versions.cs`) — create: tables and indexes.
- `src/Agentd.Migrator/Routines/{conversation,outbox,inbound,user}/*.sql` — create: the routines for these tables ([data-access.md](../../../architect/data-access.md)).
- `src/Agentd.Infrastructure.Persistence/Repositories/*.cs` — create: repositories that call those routines.

## Implementation
1. **State machine.** Allowed transitions:
   - `Running → WaitingForHuman` (`AskDeveloper`);
   - `WaitingForHuman → Running` (`ResumeWith`);
   - `WaitingForHuman → Cancelled`;
   - `WaitingForHuman → Failed` (wait timeout).

   `ResumeWith` while `Running` does **not** change state. It appends the reply to
   `Job.PendingMessages`, which is delivered as the next turn.
2. **Conversation**:
   ```csharp
   public sealed class Conversation : Entity<ConversationId>
   {
       public ProviderKey Provider { get; }
       public string ExternalConversationId { get; }   // Discord thread id | "chatId:topicId"
       public string? ExternalSpaceId { get; }          // guild/channel | chat id
       public Uri? Link { get; }
       public string? StatusMessageId { get; private set; } // for progress edit-in-place
       public DateTimeOffset OpenedAt { get; }
       public DateTimeOffset? ClosedAt { get; private set; }
   }
   ```
   `Job.OpenConversation(...)` rejects a second open conversation on the same provider.
3. **Tables** (snake_case, `timestamptz`):
   - `conversations`: `id`, `job_id` FK, `provider`, `external_conversation_id`, `external_space_id`,
     `link`, `status_message_id`, `opened_at`, `closed_at`; **unique** `(provider, external_conversation_id)`.
   - `outbound_messages`: `id bigint identity`, `job_id`, `conversation_id` FK, `provider`,
     `payload jsonb`, `kind`, `status` (`pending|sending|sent|failed|dead`), `attempts`,
     `next_attempt_at`, `external_message_id`, `last_error`, `created_at`; index on
     `(status, next_attempt_at)`.
   - `inbound_messages`: `provider`, `external_message_id`, `job_id` nullable, `user_id` nullable,
     `outcome`, `received_at`; **PK** `(provider, external_message_id)`.
   - `users`: `id`, `name` unique, `email` nullable, `roles text[]`, `is_active`, `created_at`.
   - `user_identities`: `user_id` FK, `provider`, `external_id`; **unique** `(provider, external_id)`.
4. Conversations are stored in their own table, linked to the job with `ON DELETE CASCADE` (no ORM; see As built).
5. Raise domain events from the aggregate methods, and persist them into `events` through the
   Phase 1 mechanism.

## Tests
- Domain: every allowed and forbidden transition involving `WaitingForHuman`; `ResumeWith`
  while Running queues rather than transitions; a duplicate conversation per provider is rejected.
- `ProviderKey` validation (rejects upper-case, spaces and empty values).
- Persistence (Testcontainers PostgreSQL): the migration applies cleanly; the unique constraints
  hold; round-trip of `Job` with two conversations.

## As built
- **`Conversation` is its own small aggregate** (`IConversationStore`, `conversation_*` routines), not
  a collection inside `Job`. With stored procedures, loading and saving it inside `job_save` would
  widen every job call. `Conversation.Open(job, provider, …, existing)` enforces "one open per provider"
  in the domain. The partial unique index `(job_id, provider) WHERE closed_at IS NULL` enforces it in
  the database, along with `(provider, external_conversation_id)`. Its events go to `events` through
  `conversation_insert`.
- **`Job.ResumeWith(reply, from)`:** from `WaitingForHuman` it resumes. While `Running` it appends to
  `PendingMessages` (a new `pending_messages text[]` column) and raises `DeveloperReplied(Resumed: false)`.
  `TakePendingMessages()` clears the queue. `job_save` gains a parameter; the migration drops the old signature.
- **Users** are a plain record (`User`, `UserIdentity`) for now. The tables exist; their routines come
  with T2.3. Likewise the outbox and inbound routines come with T2.5 and T2.6. This keeps this PR small.
- Verified: the migration upgrades a Phase 1 database that already has a job in it.

## Done when
- [x] Migration applies on an empty and on a Phase 1 database.
- [x] Domain tests cover all new transitions.
- [x] Architecture tests still green (Domain has no new references).
