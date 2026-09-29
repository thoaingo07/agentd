# Phase 4 — Workflow phases on MAF + ai-sdlc kit v1

**Goal:** every job follows **Design → Plan → Implement → Test → Review**, orchestrated by a
**Microsoft Agent Framework workflow** with human gates and checkpoints, and driven by the repo's
**`.agentd/` kit**. This is the biggest phase, and the one that makes agentd behave like a disciplined
engineer instead of a single prompt.

Design refs: [workflow-and-learning.md §1](../../architect/workflow-and-learning.md) ·
[orchestration-maf.md](../../architect/orchestration-maf.md) · [ai-sdlc-kit.md](../../architect/ai-sdlc-kit.md)

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

Detailed tasks: [tasks/README.md](tasks/README.md)

| ID | Task | Depends on | Size | Status |
|---|---|---|---|---|
| T4.1 | [MAF API spike (go / no-go)](tasks/01-maf-api-spike.md) | Phase 3 | S | ☐ |
| T4.2 | [Domain: phase model, gates, loop limits, artifacts](tasks/02-domain-phase-model.md) | Phase 1 | M | ☐ |
| T4.3 | [Kit v1 content + `kit.v1.json` schema](tasks/03-kit-v1-content-and-schema.md) | — | M | ☐ |
| T4.4 | [Kit domain model, layering + `ValidateKit`](tasks/04-kit-domain-and-validate.md) | T4.3 | M | ☐ |
| T4.5 | [`IKitStore` + `LoadKitForJob` (base-branch snapshot, `RequireKit`)](tasks/05-kit-store-and-load-for-job.md) | T4.4 | M | ☐ |
| T4.6 | [`InitKit` + bootstrap run + Kit PR](tasks/06-init-kit-and-bootstrap.md) | T4.3, T4.5 | L | ☐ |
| T4.7 | [Phase prompt builder](tasks/07-phase-prompt-builder.md) | T4.2, T4.5 | M | ☐ |
| T4.8 | [MAF workflow graph + executors](tasks/08-maf-workflow-and-executors.md) | T4.1, T4.2, T4.5, T4.7 | L | ☐ |
| T4.9 | [PostgreSQL checkpoint store + workflow versioning](tasks/09-postgres-checkpoint-store.md) | T4.1, T4.8 | M | ☐ |
| T4.10 | [`JobWorkflowHost`: run, release, rehydrate, recover + event mapping](tasks/10-job-workflow-host.md) | T4.8, T4.9 | L | ☐ |
| T4.11 | [MCP `complete_phase`, gated `finish`, chat gate buttons & artifacts](tasks/11-mcp-and-chat-gates.md) | T4.2, T4.10 | M | ☐ |
| T4.12 | [Guardrails: deny `.agentd/**` and protected paths](tasks/12-guardrails-protected-paths.md) | T4.5, T4.8 | S | ☐ |
| T4.13 | [UI: phase stepper, Artifacts tab, gate buttons, Repos page](tasks/13-ui-phases-artifacts-repos.md) | T4.6, T4.10, T4.11 | M | ☐ |

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
