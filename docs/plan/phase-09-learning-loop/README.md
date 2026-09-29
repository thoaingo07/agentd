# Phase 9 — Learning loop

**Goal:** every run leaves knowledge behind. Candidates are captured during and after runs, a
curator distills them into `.agentd/learnings.md` through a **Learnings PR**, and merged learnings
are fed into later runs. Citations then show which learnings actually help.

Design refs: [workflow-and-learning.md §2–§7](../../architect/workflow-and-learning.md) ·
[orchestration-maf.md §5](../../architect/orchestration-maf.md#5-non-coding-agents-on-maf)

---

## Scope

**In**

- **Capture:**
  - MCP `record_learning`;
  - **automatic signals** (test → implement loops, reviewer findings fixed or marked `wontFix`,
    developer Q&A, rejected gates, **human PR review comments** from the PR Monitor, failures, and
    turn/cost outliers);
  - the `learning_candidates` table.
- **Retrospective:** a MAF `retro-agent` with structured output after Done or Failed, plus a
  follow-up retrospective when the PR completes.
- **Distill:**
  - `LearningDistillationWorker` (every N candidates or on a schedule);
  - the MAF `curator-agent` (merge, generalize, promote, prune, budget; pinned entries preserved);
  - validation (schema, IDs, secret scan, size);
  - render `learnings.md` → the `agentd/learnings-<date>` branch → a **Learnings PR**.
- **Apply:**
  - the phase section + path-matched entries go into phase prompts (token budget);
  - MCP `get_learnings`;
  - global learnings go into the system prompt;
  - the reviewer gets the Review + Implement conventions.
- **Citations:** `complete_phase(applied_learnings[])` → `learning_citations`; failed-run citations
  are flagged for the curator.
- **Global learnings:** stored in the DB and approved on the Web UI.
- **UI:** a **Learnings** page (per-repo learnings, pending candidates, distill runs, Learnings PRs,
  global approval cards) and a **Retrospective** artifact in the session view.

**Out:** cross-repo sharing of learnings, beyond global ones.

---

## Tasks

Task index: [tasks/README.md](tasks/README.md) (the detail files are written when this phase starts).

1. Candidate model + tables; signal emitters in the existing use cases.
2. The `record_learning` / `get_learnings` MCP tools.
3. The retro agent + prompts + output schema; run after the terminal state and after PR completion.
4. `learnings.md` parser and renderer (keeps manual edits, IDs, `<!-- pinned -->`), with round-trip
   tests.
5. The curator agent + validation + the Learnings PR (via `Infrastructure.Git` + ADO).
6. Prompt injection of learnings in `IPhasePromptBuilder` (budget, highest confidence first,
   path-matched).
7. Citations + curator feedback.
8. The Learnings UI + global approval.

## Exit criteria (the demo)

- Over 3 jobs on one repo:
  - a test failure caused by a missing `docker info` check;
  - a human PR comment "use the BFF view model, not the read model";
  - a developer answer "remove legacy endpoints".

  These produce candidates, and a Learnings PR proposes 3 entries under the right phases, with
  evidence links.
- After the Learnings PR is merged, the next job's plan prompt contains the relevant entries, and
  `complete_phase` cites them. The Learnings page shows the citation counts.
- A manual edit and a `<!-- pinned -->` entry in `learnings.md` survive the next distill run.
- A candidate containing something that looks like a secret is rejected by validation.

## Risks / open questions for review

- **Curator quality and noise:** start with the threshold at 5 candidates and a daily schedule. Is
  that too slow or too fast?
- **Size budget:** 150 entries, 25 per phase. Is that reasonable?
- **Learnings PR reviewers:** who should be the required reviewers in each repo?
