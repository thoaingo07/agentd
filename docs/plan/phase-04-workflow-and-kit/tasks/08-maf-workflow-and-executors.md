# T4.8 — MAF workflow graph + executors

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 4 | T4.1 (go), T4.2, T4.5, T4.7, Phase 1 (`ClaudeCodeRunner`, publish) | L | `Agentd.Infrastructure.Orchestration` |

## Goal
Build the **versioned job workflow** on Microsoft Agent Framework: executors with stable IDs,
conditional loop edges, the `ask-port` / `gate-port` request ports, and loop limits in checkpointed
state ([orchestration-maf.md §2](../../../architect/orchestration-maf.md#2-the-job-workflow-graph)).

## Files
- `src/Agentd.Infrastructure.Orchestration/Agentd.Infrastructure.Orchestration.csproj` — create: references `Microsoft.Agents.AI.Workflows` (the version pinned in T4.1), Application and Domain.
- `…/Workflows/JobWorkflowV1.cs` — create: the graph builder (`WorkflowVersion = 1`).
- `…/Workflows/Messages.cs` — create: `PhaseInput`, `PhaseArtifact`, `TestPassed`, `TestFailed`, `ReviewApproved`, `ReviewFindings`, `DeveloperQuestion`, `DeveloperReply`, `GateRequest`, `GateDecision`, `JobResult`.
- `…/Executors/IntakeExecutor.cs`, `ClaudeCodeExecutor.cs`, `VerifyExecutor.cs`, `ReviewExecutor.cs`, `PublishExecutor.cs` — create.
- `…/Executors/PhaseSessionState.cs` — create: `{ SessionId, Profile, LoopCount, LastArtifactId }`.
- `tests/Agentd.Infrastructure.Tests/Orchestration/*` — create.

## Implementation
1. Graph (edges and routing as confirmed in T4.1):
   ```csharp
   var ask  = RequestPort.Create<DeveloperQuestion, DeveloperReply>("ask-port");
   var gate = RequestPort.Create<GateRequest, GateDecision>("gate-port");
   return new WorkflowBuilder(intake)
       .AddEdge(intake, design)            // or to plan when Design is skipped (routing by message type)
       .AddEdge(design, gate).AddEdge(gate, plan)       // gate only when the policy requires it
       .AddEdge(plan, gate).AddEdge(gate, implement)
       .AddEdge(implement, test)
       .AddEdge(test, implement)           // TestFailed
       .AddEdge(test, review)              // TestPassed
       .AddEdge(review, implement)         // ReviewFindings
       .AddEdge(review, publish)           // ReviewApproved
       .AddEdge(design, ask).AddEdge(ask, design)       // likewise for plan, implement, test
       .WithOutputFrom(publish)
       .Build();
   ```
   The **routing mechanism** (a predicate on `AddEdge` or type-based routing) is the one confirmed
   in T4.1. Executor IDs are fixed strings: `intake`, `design`, `plan`, `implement`, `test`,
   `review`, `publish`.
2. **`IntakeExecutor`:** calls `LoadKitForJob` (T4.5). On `KitMissing` / `KitInvalid` it sends a
   `GateRequest(kind: KitRequired)`. Otherwise it sends `PhaseInput` for the first non-skipped
   phase.
3. **`ClaudeCodeExecutor(phase)`**, one instance per coding phase:
   - builds the prompt (T4.7);
   - calls `IAgentRunner.RunPhaseAsync` (Phase 1 runner, extended with `--session-id` / `--resume`
     from `PhaseSessionState`);
   - maps the outcome:
     - `complete_phase` received → `CompletePhase` use case → `SendMessageAsync(PhaseArtifact)`,
       or to the gate when gated;
     - `ask_developer` → `SendMessageAsync(DeveloperQuestion)`;
     - process failure → `YieldOutputAsync(JobResult.Failed)`;
   - a `[MessageHandler]` for `DeveloperReply` and `GateDecision(rejected)` resumes the same
     session with that input;
   - `OnCheckpointingAsync` → `QueueStateUpdateAsync("state", _state)`, and
     `OnCheckpointRestoredAsync` → `ReadStateAsync<PhaseSessionState>("state")`.
4. **`VerifyExecutor` (`test`):**
   - runs `kit.verify.setup`, `build`, `test`, `lint` sequentially in the worktree (timeout per
     command) and captures trimmed output into a `test-report` artifact;
   - on failure, first gives the **main session one Test turn** to fix environmental issues. If it
     is still failing, it sends `TestFailed(report)` → `implement`, after `Job.RecordLoop`;
   - over the limit → `JobResult.Failed(LoopLimitReached)`.
5. **`ReviewExecutor` (`review`):**
   - runs a **fresh** Claude session (read-only tools: `Read Grep Glob Bash(git diff:*)`) with the
     `general` reviewer file, the diff vs base, `design.md`, `plan.md` and the acceptance criteria;
   - expects structured findings JSON, and stores a `review` artifact;
   - blocking findings → `ReviewFindings` (with the loop limit); otherwise `ReviewApproved`.
6. **`PublishExecutor`:** calls the Phase 1 `PublishPullRequest`, then
   `YieldOutputAsync(JobResult.Done(prUrl))`.
7. **Idempotency:** every executor first checks the `Job` state. If the Domain already recorded the
   phase as complete (a crash between commit and checkpoint), it skips ahead
   ([orchestration-maf.md §4](../../../architect/orchestration-maf.md#4-source-of-truth-domain-vs-maf)).

## Tests
- A workflow run with a **fake `IAgentRunner`** and a fake verify: the happy path goes through all
  five phases to `publish`.
- Test fails once, then passes → implement runs twice; the loop count is 1.
- Review findings → implement → review approved.
- Loop limit exceeded → `JobResult.Failed(LoopLimitReached)`.
- Design skipped by the policy → intake routes straight to plan.
- `ask_developer` emits a `RequestInfoEvent` carrying a `DeveloperQuestion`.
- Idempotency: with Plan already recorded as complete in the Domain, the plan executor skips.

## Done when
- [ ] Executor IDs are constants and covered by a test (the resume safety of T4.9 depends on them).
- [ ] All routing cases above pass with fakes.
- [ ] Only this project references `Microsoft.Agents.AI*` (the architecture test is extended).
