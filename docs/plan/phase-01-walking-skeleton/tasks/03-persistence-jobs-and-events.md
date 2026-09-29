# T1.3 — Persistence: jobs, events, dequeue

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.1, T1.2, Phase 0 (DbContext, first migration) | M | `Agentd.Infrastructure.Persistence` |

## Goal
Persist the `Job` aggregate and the event log in PostgreSQL. Provide a concurrency-safe dequeue so
that several scheduler loops (or, later, several hosts) never start the same job twice.

## Files
- `src/Agentd.Infrastructure.Persistence/Configurations/JobConfiguration.cs` — create: Fluent mapping.
- `src/Agentd.Infrastructure.Persistence/Configurations/EventConfiguration.cs` — create.
- `src/Agentd.Infrastructure.Persistence/Repositories/JobRepository.cs` — create: `IJobRepository`.
- `src/Agentd.Infrastructure.Persistence/EventStore.cs` — create: `IEventStore`.
- `src/Agentd.Infrastructure.Persistence/UnitOfWork.cs` — create: saves changes and appends domain events.
- `src/Agentd.Infrastructure.Persistence/Migrations/*_WalkingSkeleton.cs` — create.
- `src/Agentd.Infrastructure.Persistence/DependencyInjection.cs` — modify: register the repositories.
- `tests/Agentd.Infrastructure.Tests/Persistence/*Tests.cs` — create (Testcontainers PostgreSQL).

## Implementation
1. **`jobs` table** (extends the Phase 0 table):

   | column | type | notes |
   |---|---|---|
   | `id` | `bigint identity` | PK |
   | `work_item_id` | `int` | |
   | `repo` | `text` | |
   | `title` | `text` | |
   | `state` | `text` | enum stored as a string |
   | `branch`, `worktree_path` | `text null` | |
   | `claude_session_id` | `uuid null` | |
   | `attempt`, `resume_count` | `int` | |
   | `last_error` | `text null` | |
   | `pr_url` | `text null` | |
   | `created_at`, `updated_at` | `timestamptz` | |
   | `xmin` | system | optimistic concurrency (`UseXminAsConcurrencyToken` / `IsRowVersion`; verify against the pinned Npgsql EF version) |

   Add a **partial unique index** `(work_item_id) WHERE state NOT IN ('Done','Failed','Cancelled')`,
   so only one active job exists per work item.
2. **`events` table:** `seq bigint identity PK`, `job_id bigint FK`, `ts timestamptz`, `type text`,
   `payload jsonb`, index on `(job_id, seq)`. Partitioning is deferred to Phase 3.
3. **Value objects** map through `HasConversion` (e.g. `WorkItemId` ↔ `int`, `BranchName` ↔ `string`).
4. **Dequeue** (in a transaction):
   ```sql
   SELECT id FROM jobs WHERE state = 'Queued' ORDER BY created_at
   FOR UPDATE SKIP LOCKED LIMIT 1;
   ```
   Load the aggregate, call `BeginPreparing()`, and save in the same transaction. Use
   `FromSqlInterpolated` or raw Npgsql.
5. **Unit of work:** before `SaveChangesAsync`, collect the domain events from tracked aggregates and
   insert them as `events` rows (`type` = the event name, `payload` = JSON), all in one transaction.
6. **`EventStore.AppendAsync`** for high-volume agent output (stream-json lines): batch the inserts,
   flushing at 50 rows or 250 ms, whichever comes first.

## Tests
- `Agentd.Infrastructure.Tests` (Testcontainers PostgreSQL):
  - round-trip a `Job` through every state;
  - two concurrent `DequeueNextAsync` calls on 1 queued job → exactly one gets it;
  - the unique index rejects a second active job for the same work item;
  - optimistic concurrency conflict → `DbUpdateConcurrencyException` surfaced as a typed error;
  - domain events are written in the same transaction (a rollback leaves no events).

## Done when
- [ ] The migration applies cleanly on an empty DB and on the Phase 0 DB.
- [ ] The concurrency tests pass reliably (run 20× in CI without flakes).
- [ ] No EF types leak into Application or Domain (architecture test green).
