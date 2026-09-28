# Phase 4 — Workflow phases on MAF + ai-sdlc kit v1

**Goal:** every job follows **Design → Plan → Implement → Test → Review**, orchestrated by a
**Microsoft Agent Framework workflow** with human gates and checkpoints, and driven by the repo's
**`.agentd/` kit**. This is the biggest phase, and the one that makes agentd behave like a disciplined
engineer instead of a single prompt.

Design refs: [workflow-and-learning.md §1](../architect/workflow-and-learning.md) ·
[orchestration-maf.md](../architect/orchestration-maf.md) · [ai-sdlc-kit.md](../architect/ai-sdlc-kit.md)

---

## Scope

**In**

- **Domain:** `Job.Phase`, `CompletePhase`, gate rules, loop limits, `JobArtifact`; `Kit`,
  `KitSnapshot`, and the layering rules.
- **Application:**
  - `IJobWorkflowEngine`, `CompletePhase`, `ApproveGate` / `RejectGate`;
  - `LoadKitForJob`, `InitKit`, `ValidateKit`;
  - `IKitStore`, `IPhasePromptBuilder`.
- **`Infrastructure.Orchestration` (MAF):**
  - the versioned workflow graph;
  - `ClaudeCodeExecutor` (per phase), `VerifyExecutor`, `ReviewExecutor` (general reviewer only),
    `PublishExecutor`;
  - `ask-port` / `gate-port` request ports;
  - a **PostgreSQL checkpoint store**;
  - `JobWorkflowHost` (release on wait, rehydrate on reply, resume all on startup).
- **Kit v1** (`kit/v1/**` embedded in agentd):
  - `kit.json` + schema, `context.md`, `phases/*.md`, `templates/*.md`, `reviewers/general.md`,
    `hooks/setup.sh`;
  - the `agentd kit init | validate` CLI verbs, and the Kit PR;
  - an optional **bootstrap run** that pre-fills context and verify commands (on the single
    Phase 1 profile).
- **Guardrails:**
  - the kit is read from the base branch and snapshotted per job;
  - `.agentd/**` and `paths.protected` are denied to agents;
  - `RequireKit` is honored.
- **MCP:** `complete_phase` (artifact), and `finish` only accepted after Review.
- **Chat:** gate approve/reject buttons, and artifact attachments.
- **UI:** the phase stepper (with loop counts), an **Artifacts** tab, gate approve/reject, and a
  **Repos** page (kit status, Initialize kit).

**Out:**

- multiple model profiles; the reviewer uses the single profile (Phase 6);
- the other reviewers and PR-time review (Phase 7);
- learnings (Phase 9);
- kit upgrade (Phase 10).

---

## Tasks

1. Domain phase model + tests (skipping phases by tag, loop limits, gates).
2. Kit v1 content in `kit/v1/` + `kit.v1.json` schema; `ValidateKit` (schema, placeholders, budgets).
3. `IKitStore`: read `.agentd/` from `origin/<base>`, snapshot hashes into `job_artifacts`.
4. `InitKit`: branch `agentd/kit-init`, write defaults + baseline hashes, bootstrap run, open the
   Kit PR; refuse if `.agentd/` already exists.
5. The phase prompt builder: core rules + phase file + template + context + `CLAUDE.md`/`AGENTS.md`
   within the size budget.
6. The MAF workflow:
   - executors with stable IDs;
   - conditional edges (test failed → implement, review findings → implement);
   - loop limits in checkpointed executor state.
7. The PostgreSQL checkpoint store + `workflow_checkpoints` table; the `workflow_version` column.
8. `JobWorkflowHost`: `RequestInfoEvent` → `AskDeveloper` / gate → checkpoint → dispose; reply →
   `ResumeStreamingAsync` → `SendResponseAsync`.
9. Map MAF events → our event bus (`phase.started`, `phase.completed`, `job.waiting`).
10. Permission deny rules for `.agentd/**` and `paths.protected`, with tests.
11. UI: phase stepper, Artifacts tab, gate buttons, Repos page + `POST /api/repos/{repo}/kit/init`.

## Exit criteria (the demo)

- On a repo **without** a kit and with `RequireKit: true`, a job waits and says so in chat.
  **Initialize kit** then opens a Kit PR with pre-filled `context.md` and verify commands.
- After merging the kit, a job:
  1. produces `design.md`;
  2. produces `plan.md` → the **plan gate** shows buttons in chat and the UI → approve;
  3. implements;
  4. **tests fail once and loop back** to Implement;
  5. passes, is reviewed by `general`, and opens the PR.

  The UI shows the phase stepper, the loop count and every artifact.
- **Restarting agentd** while a job waits at the plan gate, and again while it's in Implement: both
  resume correctly from their checkpoints.
- An agent attempt to edit `.agentd/phases/implement.md` is denied.
- Editing `.agentd/phases/plan.md` on `main` changes the *next* job's plan prompt, but not a running
  job's.

## Risks / open questions for review

- **The MAF .NET API** for conditional edges and custom checkpoint stores: confirm it against the
  pinned version in a spike (1–2 days) at the start of the phase.
- **Default gates:** plan gate on, design gate off. Agreed?
- **Bootstrap run:** is it on by default, or opt-in?
- **Sessions:** one Claude session for Design → Test, with Review in a fresh session (as designed). OK?
