# T3.1 — Event store and live publisher

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | Phase 1 (`events` table, `RecordAgentOutput`) | L | Agentd.Application / Agentd.Infrastructure.Persistence |

## Goal
Every observable fact (agent output, state changes, chat messages) is appended to the `events` table
**before** it is fanned out live. The UI can then replay any job's history and follow the live
stream without gaps or duplicates. This task promotes the Phase 1 event writes into two proper ports,
`IEventStore` and `IEventPublisher`, with paging in both directions, and adds a PostgreSQL
`LISTEN/NOTIFY` listener for fan-out.

## Files
- `src/Agentd.Application/Events/IEventStore.cs` — create: `AppendAsync`, `ReadAfterAsync`, `ReadBeforeAsync`.
- `src/Agentd.Application/Events/IEventPublisher.cs` — create: `PublishAsync` and `Subscribe(filter)` returning `IAsyncEnumerable<AgentEventDto>`.
- `src/Agentd.Application/Events/AgentEventDto.cs` — create: `record(long Seq, long? JobId, DateTimeOffset Ts, string Type, JsonElement Payload)`.
- `src/Agentd.Infrastructure.Persistence/Events/PostgresEventStore.cs` — create.
- `src/Agentd.Infrastructure.Persistence/Events/NotifyEventPublisher.cs` — create: in-process `Channel` fan-out plus `NOTIFY agentd_events`.
- `src/Agentd.Infrastructure.Persistence/Events/EventNotificationListener.cs` — create: `BackgroundService` running `LISTEN agentd_events`.
- `src/Agentd.Infrastructure.Persistence/Migrations/*_EventsPartitioning.cs` — create.
- Existing Phase 1/2 emitters (`RecordAgentOutput`, state transitions, messaging) — modify: go through `IEventStore.AppendAsync`.

## Implementation
1. **Schema (migration, raw SQL where EF can't express it):**
   - `events` is partitioned **by range on `ts`, monthly**, with `seq bigint generated always as identity`,
     `job_id bigint null`, `type text`, `payload jsonb`, and `ts timestamptz`.
   - Primary key `(seq, ts)`, because a partition key must be part of the primary key. Add an index on
     `(job_id, seq)`.
   - Create the current month + next month partitions in the migration. A small startup job
     (`EnsureEventPartitions`) creates next month's partition ahead of time. Retention and dropping
     partitions are Phase 10.
2. **Append:**
   - `AppendAsync(IReadOnlyList<NewEvent>)` inserts in the caller's transaction (it uses the
     `IUnitOfWork` connection) and returns the assigned `seq` values.
   - After commit, the Application layer calls `IEventPublisher.PublishAsync`. The publisher is never
     called before commit, so readers never see an event that could be rolled back.
3. **Reading:**
   - `ReadAfterAsync(jobId?, afterSeq, limit)` returns `ORDER BY seq ASC`.
   - `ReadBeforeAsync(jobId, beforeSeq, limit)` returns `ORDER BY seq DESC`, reversed before returning
     so the result is always ascending.
   - A `null` `jobId` means the "all" stream, which only returns **summary** types: `job.*`,
     `turn.result` and `message.*`.
4. **Publisher:**
   - Keeps an in-process `Channel<AgentEventDto>` per subscriber (bounded 1000, `DropOldest`, with a
     warning log when it drops). A subscriber that falls behind re-reads from the store using its
     last `seq` (see T3.4).
   - `PublishAsync` writes to the local channels, then `NOTIFY agentd_events, '<seq>'`.
5. **Listener:**
   - Holds a dedicated `NpgsqlConnection` with `LISTEN agentd_events` and `WaitAsync` in a loop.
   - On a notification whose `seq` hasn't been seen locally, it loads the event and pushes it to the
     channels. This keeps a future out-of-process BFF in sync; in a single process, notifications
     are deduplicated by `seq`.
   - Reconnects with backoff when the connection drops.
6. **Redaction:** the payload passes through `ISecretRedactor` (from Phase 1) before it is appended.
   Redaction must happen at write time, not at read time.

## Tests
- `Infrastructure.Tests` (Testcontainers PostgreSQL):
  - append 1,000 events across 3 jobs → `ReadAfterAsync(job, 500)` returns ascending with no gaps;
  - `ReadBeforeAsync(job, 200, 50)` returns seq 150–199, ascending;
  - an event written in a rolled-back transaction is never published.
- An event appended in month M+1 lands in the right partition, and a query spanning two partitions
  works.
- Publisher: two subscribers both receive a published event, and a slow subscriber's drops are logged.
- Listener: `NOTIFY` from a second connection is delivered once, and a duplicate `seq` is ignored.

## As built
- **Partitioning:** migration 202610070001 rebuilds `events` partitioned monthly on `ts`, with
  primary key `(seq, ts)`, indexes `(job_id, seq)` and `(seq)`, and a **default partition** as a safety
  net. Existing rows keep their `seq` and the identity continues; verified on the demo database
  (348 rows → `events_2026_10`, next seq 349). `event_ensure_partitions(now)` creates this month and
  next, at startup and daily (`EventPartitionMaintenance`).
- **"After commit" by construction:** events are also inserted by routines (`job_save`, the outbox
  and conversation routines), so publishing from the application would miss them. Instead an
  `AFTER INSERT` trigger runs `pg_notify('agentd_events', seq)`, and PostgreSQL delivers it **only on
  commit**. `EventNotificationListener` (`LISTEN`, reconnect with back-off) loads the row by seq,
  ignores duplicates, and publishes to **`EventHub`**. Writers don't call a publisher.
- **Ports:** `IEventStore` (append) and a new **`IEventReader`** (`ReadAfterAsync` with null job = the
  summary "all" stream without `agent.*`, `ReadBeforeAsync` ascending, `GetAsync`), plus
  **`ILiveEvents.Subscribe(jobId?)`** (named so because CA1711 forbids a `…Stream` suffix). Each
  subscriber has a bounded 1,000-event buffer that drops the oldest and logs a warning; a lagging
  subscriber catches up from the store by seq (T3.4).
- **Redaction:** `SecretRedactor` runs in `EventStore.AppendAsync`, at write time, on the agent's
  output. It masks bearer/basic/bot tokens, JWTs, AWS/GitHub/Anthropic keys, private keys,
  connection-string passwords and quoted secret-named values. Domain events written by routines are
  agentd's own text.

## Done when
- [x] All event writers go through `IEventStore` or routines; live fan-out covers both (trigger).
- [x] `events` is partitioned monthly, and the next partition is created automatically.
- [x] Paging works in both directions and is covered by tests.
- [x] Live fan-out only happens after commit, and it works for multiple subscribers.
- [x] Payloads are redacted before they are stored.
