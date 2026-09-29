# T1.1 — Domain: `Job` aggregate and state machine

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | Phase 0 (solution skeleton) | M | `Agentd.Domain` |

## Goal
Model a job as an aggregate whose state can only change through methods. Invalid transitions can't
be expressed. Phase 1 needs only the minimal lifecycle; later phases add `WaitingForHuman` (Phase 2)
and the phase model (Phase 4) without rewriting this.

## Files
- `src/Agentd.Domain/Common/AggregateRoot.cs` — create: base class with `Id`, a domain-event list, `Raise()`, `DequeueEvents()`.
- `src/Agentd.Domain/Common/DomainError.cs` — create: `InvalidTransition`, `NotFound`, … (a typed error, not an exception).
- `src/Agentd.Domain/Common/Result.cs` — create: `Result` / `Result<T>` (success, or a `DomainError`).
- `src/Agentd.Domain/Jobs/Job.cs` — create: the aggregate.
- `src/Agentd.Domain/Jobs/JobState.cs` — create: an enum.
- `src/Agentd.Domain/Jobs/ValueObjects/*.cs` — create: `JobId`, `WorkItemId`, `RepositoryName`, `BranchName`, `ClaudeSessionId`, `WorktreePath`, `PullRequestUrl`.
- `src/Agentd.Domain/Jobs/Events/*.cs` — create: `JobQueued`, `JobStarted`, `JobFinished`, `PullRequestCreated`, `JobFailed`, `JobCancelled`, `JobRecovered`.
- `tests/Agentd.Domain.Tests/Jobs/JobTransitionTests.cs` — create.

## Implementation
1. **States:** `Queued → Preparing → Running → Publishing → Done`, plus `Failed` and `Cancelled` as
   terminal states. `Failed → Queued` is allowed via `Retry()` (increments `Attempt`).
2. **Transition methods** (each returns `Result` and raises one event):
   ```csharp
   public static Job Create(WorkItemId wi, RepositoryName repo, string title, IClock clock);   // → Queued
   public Result BeginPreparing();                                   // Queued → Preparing
   public Result Start(WorktreePath path, BranchName branch, ClaudeSessionId session); // Preparing → Running
   public Result Finish(PullRequestDraft draft);                     // Running → Publishing
   public Result Complete(PullRequestUrl url);                       // Publishing → Done
   public Result Fail(string reason);                                // any non-terminal → Failed
   public Result Cancel(string by);                                  // Queued/Preparing/Running/Publishing → Cancelled
   public Result Retry();                                            // Failed → Queued
   public Result MarkRecovered();                                    // Running → Running (records a JobRecovered event, bumps ResumeCount)
   ```
   Use a private `Require(params JobState[] allowed)` helper that returns `DomainError.InvalidTransition(from, to)`.
3. **Value objects:** `readonly record struct`s with validation in their factories:
   - `BranchName.For(WorkItemId, title, prefix)` builds `ai/<id>-<slug>`: lowercase, `[a-z0-9-]`,
     slug ≤ 40 characters, no trailing `-`;
   - `WorkItemId` > 0;
   - `ClaudeSessionId` is a GUID.
4. **`PullRequestDraft`** record: `Title` (≤ 200 characters), `Description`, `Summary`.
5. **Other properties:** `CreatedAt`, `UpdatedAt`, `Attempt`, `ResumeCount`, `LastError`, `PrUrl`.
   No EF attributes; the mapping is in T1.3.
6. **Clock:** `IClock` lives in the Domain as an interface; the Host provides the implementation.

## Tests
- `Agentd.Domain.Tests`: a table-driven test over **every (state, method) pair**. Allowed pairs change
  state and raise exactly one expected event; every other pair returns `InvalidTransition` and leaves
  the state unchanged.
- `BranchName` slug cases: unicode, punctuation, very long titles, an empty title (→ `ai/<id>`).
- `Retry` increments `Attempt` and clears `LastError`.

## Done when
- [ ] All transitions are covered by tests; the Domain project still references only the BCL (architecture test green).
- [ ] No public setters on `Job`; state changes only through the methods above.
- [ ] Domain events are raised and can be dequeued.
