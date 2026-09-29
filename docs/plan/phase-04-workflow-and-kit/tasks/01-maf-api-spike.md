# T4.1 — Spike: verify the MAF .NET APIs we depend on

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | Phase 3 done | S (1–2 days) | `spikes/MafSpike` (throwaway console app, not in the solution) |

## Goal
Before building the workflow, prove that the Microsoft Agent Framework .NET package version we pin
supports everything the design relies on. The spike ends with a **go / no-go** note. On no-go, it
names the fallback for each missing feature, so T4.8–T4.10 are not built on assumptions.

## Files
- `spikes/MafSpike/MafSpike.csproj` — create: console app referencing `Microsoft.Agents.AI.Workflows`
  (the version from `Directory.Packages.props`; pin the latest stable release at spike time).
- `spikes/MafSpike/Program.cs` — create: the five experiments below.
- `docs/plan/phase-04-workflow-and-kit/tasks/01-maf-api-spike-result.md` — create: findings + decision.

## Implementation
Build a toy workflow that mirrors the real one's shape: `intake → work → test ⟲ work → done`, plus
an `ask-port`.

1. **Executors with stable IDs.** `partial` classes deriving from `Executor("work")`, with
   `[MessageHandler]` methods; one `Executor<TIn>` with an overridden `HandleAsync`.
2. **Conditional edges.** Route `TestFailed` back to `work` and `TestPassed` to `done`.
   - Try an `AddEdge` overload with a condition/predicate, or type-based routing where different
     message types go to different targets. Record which one exists (**confirmed in T4.1 spike**).
   - Loop counter kept in executor state; stop after N loops.
3. **Human-in-the-loop.**
   - `RequestPort.Create<Question, Answer>("ask-port")`, with an edge `work → ask-port → work`.
   - Consume `RequestInfoEvent` from `run.WatchStreamAsync()`, answer with
     `run.SendResponseAsync(evt.Request.CreateResponse(answer))`.
   - Confirm the answer is routed back to the executor that asked.
4. **Checkpointing with a custom store.**
   - Run with `CheckpointManager.CreateInMemory()`, then find the abstraction for a **custom
     (PostgreSQL-backed) store**: the interface or base class name, how payloads are serialized
     (JSON?), and the `CheckpointInfo` shape (**confirmed in T4.1 spike**).
   - Implement a file-backed version of it in the spike, to prove it can be persisted outside
     memory.
   - `OnCheckpointingAsync` → `context.QueueStateUpdateAsync("state", …)`, and
     `OnCheckpointRestoredAsync` → `context.ReadStateAsync<T>("state")`.
5. **Release and rehydrate across processes.**
   - Process A runs until the `RequestInfoEvent`, saves the checkpoint, and exits.
   - Process B rebuilds the workflow (same IDs) and calls
     `InProcessExecution.ResumeStreamingAsync(workflow, checkpoint, checkpointManager)`.
   - Verify the **pending request is re-emitted** as a `RequestInfoEvent`, then answer it and finish.
   - Repeat with a changed executor ID, and confirm resume fails loudly. That shapes the versioning
     rules in T4.9.
6. **Agent executor identity.** Add a `ChatClientAgent` with a fixed `ChatClientAgentOptions.Id` and
   `Name` (a stub `IChatClient` is fine), and confirm resume works when it is rebuilt.

## Tests
- The spike is its own test: every experiment prints PASS/FAIL. No unit tests are kept.

## Done when
- [ ] The result file records, for each of the 6 items: the API used (exact type and method
      names), PASS/FAIL, and notes.
- [ ] The custom checkpoint store abstraction and the conditional-edge API are named explicitly;
      T4.8/T4.9 are updated to use those names.
- [ ] Go / no-go decision recorded. If no-go on any item, the fallback is written down, e.g.
      "loops via type-routed messages instead of predicates" or "own checkpoint persistence around
      the in-memory manager".
- [ ] The pinned package version is written into `Directory.Packages.props` (in T4.8's PR).
