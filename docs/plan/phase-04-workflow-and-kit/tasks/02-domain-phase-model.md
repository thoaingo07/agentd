# T4.2 — Domain: phase model, gates, loop limits, artifacts

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | Phase 1 (`Job` aggregate) | M | `Agentd.Domain`, `Agentd.Infrastructure.Persistence` |

## Goal
Extend the `Job` aggregate so the business state of the five-phase workflow lives in the Domain:
the current phase, gates, loop counters, phase skipping and artifacts. MAF (T4.8) only drives
transitions through use cases, and the Domain is authoritative
([orchestration-maf.md §4](../../../architect/orchestration-maf.md#4-source-of-truth-domain-vs-maf)).

## Files
- `src/Agentd.Domain/Jobs/JobPhase.cs` — create: `enum JobPhase { Design, Plan, Implement, Test, Review }`.
- `src/Agentd.Domain/Jobs/PhasePolicy.cs` — create: gates, loop limits and skipped phases (a value object built from the kit).
- `src/Agentd.Domain/Jobs/Job.cs` — modify: `Phase`, `CompletePhase`, `OpenGate`, `ApproveGate`, `RejectGate`, `RecordLoop`.
- `src/Agentd.Domain/Jobs/JobArtifact.cs` — create: `{ Phase, Kind, Markdown, CreatedAt }`.
- `src/Agentd.Domain/Jobs/Events/*.cs` — create: `PhaseStarted`, `PhaseCompleted`, `GateOpened`, `GateDecided`, `PhaseLooped`.
- `src/Agentd.Infrastructure.Persistence/Configurations/*` + a migration — create: `job_phases`, `job_artifacts`.

## Implementation
1. `PhasePolicy` (immutable):
   ```csharp
   public sealed record PhasePolicy(
       IReadOnlySet<JobPhase> Gates,          // default { Plan }
       int MaxFixLoops, int MaxReviewLoops,   // defaults 3, 2
       IReadOnlySet<JobPhase> Skipped);       // from kit skipPhases.whenTags
   ```
2. `Job` transitions (all throw `DomainException(InvalidTransition)` when misused):
   - `StartPhase(JobPhase p)`: only while `State == Running`, and `p` must be the next non-skipped
     phase or a loop target.
   - `CompletePhase(JobPhase p, JobArtifact a)`: stores the artifact. If `p` is gated, it calls
     `OpenGate(p)` → `State = WaitingForHuman`.
   - `ApproveGate(p, by)` → back to `Running`, next phase. `RejectGate(p, by, reason)` → re-run
     `p` with the reason as input.
   - `RecordLoop(from: Test|Review)` → `Implement`, incrementing the counter. When a limit is
     exceeded it returns `LoopLimitReached`, and the caller fails the job.
   - `Finish(...)` (existing) is allowed **only after `Review` is completed**.
3. Phase skipping: `NextPhase()` skips the phases in `Policy.Skipped` (e.g. `ai-kind:deps` skips
   Design).
4. Tables:
   - `job_phases(job_id, phase, started_at, completed_at, loop_count, gate_status, summary)`,
     primary key `(job_id, phase)`;
   - `job_artifacts(id, job_id, phase, kind, markdown, created_at)`, where `kind` is one of `design`,
     `plan`, `test-report`, `review` or `kit-snapshot`.
5. Domain events are raised for every transition, and published after commit (existing mechanism).

## Tests
- Full transition matrix: every phase × every command → allowed or `InvalidTransition`.
- Gate on Plan: `CompletePhase(Plan)` → `WaitingForHuman`; approve → `Implement`; reject → `Plan`
  again, carrying the reason.
- Loop limits: the 4th Test → Implement loop with `MaxFixLoops = 3` returns `LoopLimitReached`.
- Skipping: with Design skipped, the first phase is Plan.
- `Finish` before Review completes → `InvalidTransition`.
- Persistence round trip (Testcontainers): the phases and artifacts survive a save and reload.

## Done when
- [ ] All transitions are covered by unit tests, and Domain still has no references beyond the BCL
      (the architecture test is green).
- [ ] The migration is applied, and the tables match the columns above.
- [ ] Domain events are emitted for every transition.
