# T2.1 — Domain model and database schema for messaging

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | Phase 1 (`Job` aggregate, `AgentdDbContext`) | M | Agentd.Domain, Agentd.Infrastructure.Persistence |

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
- `src/Agentd.Infrastructure.Persistence/Configurations/*.cs` — create: Fluent mappings.
- `src/Agentd.Infrastructure.Persistence/Migrations/<ts>_Messaging.cs` — create: migration.

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
4. `Job` loads its conversations as an owned collection (EF `HasMany` with cascade delete).
5. Raise domain events from the aggregate methods, and persist them into `events` through the
   Phase 1 mechanism.

## Tests
- Domain: every allowed and forbidden transition involving `WaitingForHuman`; `ResumeWith`
  while Running queues rather than transitions; a duplicate conversation per provider is rejected.
- `ProviderKey` validation (rejects upper-case, spaces and empty values).
- Persistence (Testcontainers PostgreSQL): the migration applies cleanly; the unique constraints
  hold; round-trip of `Job` with two conversations.

## Done when
- [ ] Migration applies on an empty and on a Phase 1 database.
- [ ] Domain tests cover all new transitions.
- [ ] Architecture tests still green (Domain has no new references).
