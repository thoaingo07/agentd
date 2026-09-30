# T1.11 — Host workers: polling, scheduler, startup recovery

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2–T1.10 | M | `Agentd.Host` |

## Goal
Run the daemon loop: poll Azure DevOps, start queued jobs up to `MaxConcurrent`, and recover
cleanly after a restart. That includes **resuming the same Claude session** for jobs that were
running when agentd stopped.

## Files
- `src/Agentd.Host/Workers/WorkItemPollingWorker.cs` — create.
- `src/Agentd.Host/Workers/SchedulerWorker.cs` — create.
- `src/Agentd.Host/Workers/StartupRecovery.cs` — create (an `IHostedService` that runs first).
- `src/Agentd.Application/Jobs/RecoverJobsOnStartup.cs` — create.
- `src/Agentd.Host/Program.cs` — modify: register the workers.
- `tests/Agentd.Application.Tests/Jobs/RecoverJobsOnStartupTests.cs` — create.

## Implementation
1. **`WorkItemPollingWorker`:**
   - a `PeriodicTimer(PollInterval)` → a DI scope → `PollWorkItems`;
   - errors are logged and the loop continues;
   - after a failure, backs off exponentially up to 5 minutes;
   - the polling interval and the next run time are exposed in the health details.
2. **`SchedulerWorker`:**
   - a `SemaphoreSlim(MaxConcurrent)` tracks running agents;
   - loop: wait for a slot → `StartNextJob` (dequeue with `SKIP LOCKED`, T1.3) → if nothing is
     queued, release the slot and wait (a 2 s delay, or a signal when a job is queued);
   - when a run finishes, `HandleAgentExit` or `PublishPullRequest` runs and the slot is released.
   - The worker also retries `Publishing` jobs with a `LastError` (backoff).
3. **`RecoverJobsOnStartup`** (runs before the other workers start):
   1. prune worktrees for every repository;
   2. `Preparing` jobs → back to `Queued` (the worktree gets re-created or reused);
   3. `Running` jobs whose process isn't alive (always true after a restart):
      - issue a new MCP token and rewrite `mcp.json`;
      - `Job.MarkRecovered()`;
      - enqueue a **resume run**: `claude --resume <session>` with the prompt "agentd restarted;
        continue where you left off. Call finish when done.";
   4. `Publishing` jobs → go through publish again (idempotent, T1.10).
4. **Graceful shutdown:**
   - on `StopAsync`, stop taking new jobs;
   - wait up to `ShutdownGrace` (default 10 s) for runs to finish;
   - then kill the process trees. The jobs stay `Running`, so recovery resumes them next time.

## Tests
- Application tests with fakes: recovery moves each state correctly; a resume run uses `--resume`
  with the same session ID; `MaxConcurrent` is respected (3 queued, `MaxConcurrent` = 2 → 2 start).
- A manual demo check: kill agentd during a run → restart → the transcript continues in the same
  session.

## As built
- **`JobDispatcher`** (Application, singleton) holds the concurrency slots, runs agents in the background,
  reports exits via `HandleAgentExit` and implements graceful shutdown, so it is unit-tested with fakes.
  The Host workers only loop: `WorkItemPollingWorker` (poll, claim, wake the scheduler; back-off
  `PollInterval` × 2^failures up to `MaxPollBackoff`) and `SchedulerWorker` (resume recovered runs first,
  then dequeue; runs `RetryDuePublishes` every 30 s when idle).
- **Options** `Agentd:Scheduler`: `Enabled`, `PollInterval`, `MaxPollBackoff`, `MaxConcurrent` (2),
  `IdleDelay` (2 s), `ShutdownGrace` (10 s). `Enabled=false` turns off polling, scheduling and recovery
  (UI tests, and **the Development config until the first live run is approved**).
- **Recovery** (`StartupRecovery`, the first hosted service): seeds repositories from
  `Agentd:Repositories:Items` (`SeedRepositories`: skips entries already registered with the same settings),
  prunes worktrees, then `Preparing` → `Job.Requeue` (new transition, event `JobRequeued`),
  orphaned `Running` → `MarkRecovered` + resume run, `Publishing` → publish again unless a retry is
  scheduled later. A fresh MCP token and `mcp.json` come for free: the runner issues them per run.
  Failures are logged; the host stays up for the UI.
- **Shutdown:** agents killed after the grace period skip `HandleAgentExit`, so their jobs stay `Running`.
- **Host wiring:** Application, Azure DevOps, Git, Claude and MCP are registered. Authentication and
  authorization run after routing (`UseWebHosting(afterRouting: ...)`). When `Agentd:Claude:McpUrl` is
  empty, it resolves to the server's own address + `/mcp` (`McpUrlFromServer`).
- A `workers` health check reports poll timing and running agents (degraded while polling fails).

## Done when
- [ ] The exit-criteria demos work: two jobs in parallel; restart mid-run resumes the same session.
- [ ] No job is ever started twice (the dequeue test + logs during the demo).
