# T4.10 — `JobWorkflowHost`: run, release on wait, rehydrate, recover + event mapping

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | T4.8, T4.9, Phase 2 (`AskDeveloper`, messaging), Phase 3 (event bus) | L | `Agentd.Application` (port), `Agentd.Infrastructure.Orchestration`, `Agentd.Host` |

## Goal
Implement `IJobWorkflowEngine` on MAF:

- start workflows;
- turn `RequestInfoEvent`s into chat questions or gates, then **checkpoint and dispose the run**;
- **rehydrate** it when a reply arrives;
- resume every non-final job at startup;
- map MAF events onto agentd's event bus, so the Web UI keeps working unchanged.

## Files
- `src/Agentd.Application/Workflow/IJobWorkflowEngine.cs` — create:
  `StartAsync(JobId)`, `ResumeAsync(JobId, HumanResponse)`, `CancelAsync(JobId)`, `RecoverAllAsync()`.
- `src/Agentd.Infrastructure.Orchestration/Hosting/JobWorkflowHost.cs` — create: the implementation.
- `src/Agentd.Infrastructure.Orchestration/Hosting/WorkflowEventMapper.cs` — create.
- `src/Agentd.Application/Jobs/SubmitDeveloperMessage.cs`, `ApproveGate.cs`, `RejectGate.cs` — modify/create: call `IJobWorkflowEngine.ResumeAsync`.
- `src/Agentd.Host/Workers/SchedulerWorker.cs` — modify: `StartNextJob` → `IJobWorkflowEngine.StartAsync`.
- `src/Agentd.Host/Startup/StartupRecovery.cs` — modify: `RecoverAllAsync()`.

## Implementation
1. **Run loop** (one per active job, as a background task):
   ```csharp
   await using var run = await InProcessExecution.RunStreamingAsync(workflow, input, checkpointManager);
   await foreach (var evt in run.WatchStreamAsync(ct))
   {
       switch (evt)
       {
           case SuperStepCompletedEvent s:
               await SaveCheckpointPointerAsync(job, s.CompletionInfo?.Checkpoint); break;
           case RequestInfoEvent r:
               await HandleRequestAsync(job, r);          // → AskDeveloper / OpenGate / KitRequired
               pending = r;                                // wait for the superstep checkpoint, then release
               break;
           case WorkflowOutputEvent o:
               await CompleteJobAsync(job, (JobResult)o.Data); return;
       }
       await mapper.PublishAsync(job, evt);               // phase.started / phase.completed / job.waiting
       if (pending is not null && IsCheckpointed(pending)) { break; }  // release: dispose the run
   }
   ```
   Once the pending request is covered by a checkpoint, the run is **disposed**. Nothing stays in
   memory while the job waits.
2. **`HandleRequestAsync`:**
   - `DeveloperQuestion` → `AskDeveloper` (chat + `WaitingForHuman`);
   - `GateRequest(Plan|Design)` → `OpenGate` + chat buttons (T4.11);
   - `GateRequest(KitRequired)` → a chat notice + the `KitWatcher` (T4.5).
3. **`ResumeAsync(jobId, response)`:**
   - load `current_checkpoint_id` and the builder for the job's `workflow_version`;
   - `InProcessExecution.ResumeStreamingAsync(workflow, checkpoint, checkpointManager)`;
   - wait for the **re-emitted** `RequestInfoEvent`, then
     `run.SendResponseAsync(evt.Request.CreateResponse(mapped))`;
   - continue the run loop above;
   - guard: `ResumeAsync` is serialized per job with a PostgreSQL advisory lock, so two replies can't
     resume twice. "First answer wins" is already enforced by the Domain.
4. **`RecoverAllAsync()`** at startup: every job in `Running` or `WaitingForHuman` with a checkpoint.
   - `Running` → resume from the checkpoint. The executor's idempotency check (T4.8) skips completed
     work, and an interrupted Claude turn resumes via `--resume`.
   - `WaitingForHuman` → nothing to do until a reply arrives. Resume is lazy.
5. **`CancelAsync`:** cancel the run token, kill the Claude process (through the runner), and call
   `Job.Cancel`.
6. **`WorkflowEventMapper`** onto the Phase 3 event bus:
   - executor invoked/completed → `phase.started` / `phase.completed` (phase, loop count);
   - `RequestInfoEvent` → `job.waiting` (kind: question/gate/kit);
   - `WorkflowOutputEvent` → `job.state_changed`.

   The event names are the ones the UI already consumes.
7. **Concurrency:** `MaxConcurrent` counts only jobs with a live run. Released (waiting) jobs don't
   use a slot.

## Tests
- Integration (Testcontainers + fakes):
  - start → `ask_developer` → the run is released (no live run object) → `ResumeAsync` with a reply
    → the same executor receives it → completes;
  - plan gate: approve → implement; reject → plan again, with the reason.
- **Restart simulation:** a new host instance, then `RecoverAllAsync` → a `Running` job resumes and
  a waiting job resumes on reply.
- Two concurrent replies → only one resume (the advisory lock).
- Event mapping: the expected `phase.*` / `job.waiting` events are published in order.
- `CancelAsync` during Implement → the process is killed and the job is `Cancelled`.

## Done when
- [ ] Exit criterion: restarting agentd while a job waits at the plan gate, and again during
      Implement, resumes both correctly.
- [ ] No in-memory workflow runs exist for waiting jobs (asserted via a host diagnostics counter).
- [ ] The Phase 3 UI shows phase events without UI code changes (except T4.12 additions).
