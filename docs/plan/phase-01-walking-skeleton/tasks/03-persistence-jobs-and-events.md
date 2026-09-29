# T1.3 — Persistence: jobs, events, dequeue (PostgreSQL functions)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.1, T1.2, Phase 0 (migration runner, first migration) | M | `Agentd.Infrastructure.Persistence` |

## Goal
Persist the `Job` aggregate and the event log in PostgreSQL **through functions and procedures only**
([data-access.md](../../../architect/data-access.md)). Provide a concurrency-safe dequeue, so that
several scheduler loops (or, later, several hosts) never start the same job twice.

## Files
- `src/Agentd.Infrastructure.Persistence/Database/Migrations/0002_walking_skeleton.sql`: create.
  New `jobs` columns, `events.type` index, and the unique active-job index.
- `src/Agentd.Infrastructure.Persistence/Database/Routines/job/*.sql`: create. `job_create`,
  `job_get`, `job_dequeue`, `job_save`, `job_list_active`.
- `src/Agentd.Infrastructure.Persistence/Database/Routines/event/*.sql`: create. `event_append`,
  `event_append_batch`, `event_page`.
- `src/Agentd.Infrastructure.Persistence/Repositories/JobRepository.cs`: create. `IJobRepository`.
- `src/Agentd.Infrastructure.Persistence/Repositories/EventStore.cs`: create. `IEventStore`.
- `src/Agentd.Infrastructure.Persistence/Repositories/SqlErrors.cs`: create. SQLSTATE → Application `Error`.
- `src/Agentd.Infrastructure.Persistence/DependencyInjection.cs`: modify. Register the repositories.
- `tests/Agentd.Infrastructure.Tests/Persistence/*Tests.cs`: create (MSTest, Testcontainers).

## Implementation
1. **`jobs` table** (extends the Phase 0 table, in migration `0002`):

   | column | type | notes |
   |---|---|---|
   | `id` | `bigint generated always as identity` | PK |
   | `work_item_id` | `int not null` | |
   | `repo`, `title` | `text` | |
   | `state` | `text not null` | CHECK against the allowed state names |
   | `branch`, `worktree_path` | `text null` | |
   | `claude_session_id` | `uuid null` | |
   | `attempt`, `resume_count` | `int not null default 0` | |
   | `last_error`, `pr_url`, `claimed_by` | `text null` | |
   | `created_at`, `updated_at` | `timestamptz not null default now()` | |
   | `version` | `bigint not null default 1` | optimistic concurrency |

   Add a **partial unique index** `(work_item_id) WHERE state NOT IN ('Done','Failed','Cancelled')`,
   so only one active job exists per work item.
2. **Routines** (all in schema `agentd`, following the naming, SQLSTATE and version conventions):
   - `job_create(p_work_item_id, p_repo, p_title) RETURNS TABLE(...)`: inserts a `Queued` job, and
     raises `AG409` if an active job already exists (catch `unique_violation`, re-raise as `AG409`).
   - `job_get(p_id)`: returns the row, or raises `AG404`.
   - `job_dequeue(p_worker)`: `FOR UPDATE SKIP LOCKED` → `Preparing`, in one statement (example in
     data-access.md §3).
   - `job_save(p_id, p_expected_version, p_state, …, p_events jsonb)`: updates the row `WHERE version
     = p_expected_version` (raising `AG409` on a mismatch), **and appends the aggregate's domain
     events** (a `jsonb` array) to `events` in the **same transaction**. Returns the new `version`.
   - `job_list_active()`: used by recovery and the scheduler.
   - `event_append(p_job_id, p_type, p_payload)` / `event_append_batch(p_rows jsonb)`: returns the
     `seq` values.
   - `event_page(p_job_id, p_after_seq, p_limit)`.
3. **Repository mapping:**
   - `JobRepository` maps the rows to the `Job` aggregate (it rehydrates through a Domain factory
     method; no reflection into private fields) and keeps the `version` for the next save;
   - it **opens one connection per call** from `NpgsqlDataSource` and never shares a connection
     across threads;
   - Dapper is used for result mapping only.
4. **Domain events** are collected from the aggregate before `job_save`, and serialized with their
   type name, so saving the state and recording its events is one atomic database call.
5. **`EventStore.AppendAsync`** for high-volume agent output (stream-json lines): an in-memory
   buffer flushed through `event_append_batch` at 50 rows or 250 ms, whichever comes first. The
   buffer is a `Channel<T>` with a single consumer, so it's thread-safe.
6. **Errors:** `SqlErrors` maps `PostgresException.SqlState` to Application errors: `AG404` →
   `NotFound`, `AG409` → `ConcurrencyConflict`, `AG422` → `InvalidTransition`. Anything else stays
   an exception.

## Tests
- `Agentd.Infrastructure.Tests` (MSTest, `[TestCategory("Integration")]`, one Testcontainers
  PostgreSQL per assembly, with migrations applied in `[AssemblyInitialize]`):
  - round-trip a `Job` through every state via `job_save`, with `version` incrementing;
  - **parallel dequeue:** 20 queued jobs and 20 parallel `DequeueAsync` tasks → 20 distinct jobs,
    none claimed twice; 1 queued job and 5 parallel callers → exactly one gets it;
  - `job_create` for a work item that already has an active job → `ConcurrencyConflict` (`AG409`);
  - a stale `p_expected_version` → `ConcurrencyConflict`, and neither the row nor any events change;
  - `job_save` events: a forced failure after the update (test hook) leaves no partial events;
  - `event_append_batch` ordering and `event_page` paging.

## Done when
- [ ] `0002` applies cleanly on an empty database and on the Phase 0 database, and the routines
      re-apply idempotently.
- [ ] The concurrency tests pass reliably (run 20× in CI without flakes).
- [ ] No Npgsql or Dapper types leak into Application or Domain (architecture tests green).
