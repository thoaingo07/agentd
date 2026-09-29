# T4.9 — PostgreSQL checkpoint store + workflow versioning

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | T4.1, T4.8 | M | `Agentd.Infrastructure.Orchestration`, `Agentd.Infrastructure.Persistence` |

## Goal
Persist MAF checkpoints in PostgreSQL so a workflow can be released from memory while it waits,
survive restarts, and be rehydrated. Record the **workflow version** per job, so a graph change
never breaks resuming an in-flight job
([orchestration-maf.md §3](../../../architect/orchestration-maf.md#3-long-waits-checkpoint-release-resume)).

## Files
- `src/Agentd.Infrastructure.Orchestration/Checkpoints/PostgresCheckpointStore.cs` — create: implements the MAF custom checkpoint store abstraction (**interface or base type confirmed in T4.1 spike**).
- `src/Agentd.Infrastructure.Orchestration/Checkpoints/CheckpointManagerFactory.cs` — create: builds a `CheckpointManager` over the store, scoped to a job.
- `src/Agentd.Infrastructure.Orchestration/Workflows/WorkflowRegistry.cs` — create: `version → builder`.
- `src/Agentd.Infrastructure.Persistence/Migrations/*` — create: the `workflow_checkpoints` table; `jobs.workflow_version`, `jobs.current_checkpoint_id`.

## Implementation
1. Table:
   ```sql
   create table workflow_checkpoints (
     job_id           bigint      not null references jobs(id),
     checkpoint_id    text        not null,
     superstep        int         not null,
     workflow_version int         not null,
     payload          jsonb       not null,
     created_at       timestamptz not null default now(),
     primary key (job_id, checkpoint_id)
   );
   create index on workflow_checkpoints (job_id, created_at desc);
   ```
2. `PostgresCheckpointStore` stores and loads payloads with the serializer MAF provides. It is
   **scoped to one job ID**: listing only returns that job's checkpoints.
3. On each `SuperStepCompletedEvent` with a checkpoint (handled in T4.10), update
   `jobs.current_checkpoint_id` in the same transaction as any Domain change made in that superstep,
   where possible.
4. **Retention:** keep the latest 10 checkpoints per job plus the final one, pruning in the same
   write. Full cleanup happens with the job's events (Phase 10).
5. **Versioning:**
   - `WorkflowRegistry` maps `1 → JobWorkflowV1.Build(...)`;
   - new jobs use `Current`, and a job's `workflow_version` is fixed when it is created;
   - resuming always uses the builder for the job's version. At startup, fail loudly if an
     in-flight job's version is no longer registered;
   - the rule for retiring a version (restart the phase on the new version with the artifact
     handoff) is documented in code comments and the runbook. Implementing it is Phase 10.
6. **Security:** the table is written only by the daemon's DB role. Checkpoints are never imported
   or loaded from outside.

## Tests
- Integration (Testcontainers):
  - run a toy workflow to a request port and save the checkpoint;
  - **new process scope:** rebuild the workflow and resume from the stored checkpoint; the pending
    request is re-emitted and completing it succeeds.
- Scoping: job A's store can't read job B's checkpoints.
- Retention: after 15 supersteps, 10 + 1 checkpoints remain.
- Version guard: a job whose version isn't registered → startup error that names the job.

## Done when
- [ ] Resume works across process restarts against a real PostgreSQL.
- [ ] Every job row has `workflow_version` and `current_checkpoint_id` set while it's running or waiting.
- [ ] Retention and scoping are tested.
