# Phase 8 — PR Monitor (fix rounds) + hotfix & backport

> **Note (2026-10-03):** [Phase 2b](../phase-02b-job-lifecycle/README.md) delivers a light, single-session version of parts of this phase (phases and plan gate / PR review fix loop / knowledge hand-off). Build on it rather than re-building it.

**Goal:** keep PRs moving. agentd **fixes** review comments, red CI and merge conflicts on the PR
branch and replies to and resolves the threads. **Fix now** lets you push a hotfix to any PR, and an
expedited **hotfix flow** branches from a release branch and backports to `main`.

Design refs: [pr-reviewer-and-monitor.md §2, §4, §5](../../architect/pr-reviewer-and-monitor.md)

---

## Scope

**In**

- **Kit v1.2:** `phases/pr-fix.md`, `phases/hotfix.md`, `templates/pr-fix-report.md`,
  `templates/hotfix.md`, and the `kit.json` `pr.monitor` and `hotfix` sections.
- **Domain:** `FixRound`; `JobKind.PrFollowUp`, `Hotfix`, `Backport`; round limits; the hotfix plan
  gate that can't be disabled.
- **`PullRequestMonitorWorker`:**
  - change detection with cursors: threads, votes, policy and build failures, `mergeStatus`,
    iterations, completed/abandoned;
  - debouncing;
  - scope `agentd | opted-in | all`.
- **Build failure extraction:** the build timeline → failed tasks → trimmed logs.
- **The `PrFollowUp` MAF workflow:** Triage (structured) → Fix (resume the origin session, or a
  handoff) → Test → Review → **push (no force-push; merge the target to resolve conflicts)** → reply
  to and resolve threads → report.
- **Trigger authorization:**
  - `Users[].Identities.AzureDevOps` + `trustedGroups`;
  - explicit trigger (`@agentd fix` or an Operator action) for PRs agentd didn't create;
  - never on forks or external contributors;
  - ignore agentd's own comments.
- **Fix now** (with an optional instruction) from the UI and chat (`/fix`).
- **Hotfix:**
  - branch from `release/*`;
  - the expedited workflow (Plan → Implement → Test → Review) with the mandatory plan gate;
  - a PR with the hotfix template + auto-review;
  - after merge, a **backport** by cherry-pick → a backport PR (with a fix round on conflicts).
- **UI:**
  - Monitor on/off, Fix now and Create hotfix actions;
  - a **Fix rounds** tab;
  - dashboard badges ("Fixing r2", "Monitored").
- **Chat:** `/monitor`, `/fix`, `/hotfix`; escalation of `disagree/unclear` threads.

**Out:** auto-merging the target when it moves (config flag, off; evaluate in Phase 10).

---

## Tasks

Task index: [tasks/README.md](tasks/README.md) (the detail files are written when this phase starts).

1. Kit v1.2 files + schema; `ValidateKit` updates.
2. Monitor worker + cursors (`pull_requests.cursors`), change events, debouncing.
3. The build log extractor (fixtures for dotnet, npm and lint failures).
4. The authorization rules for triggers + tests (untrusted author, fork, missing mention, own comment).
5. The triage agent (MAF, structured output) + tests on recorded thread sets.
6. The `PrFollowUp` workflow + `fix_rounds` table; push rules (a guard that rejects force-push);
   merge-based conflict resolution.
7. Thread replies and status updates; round report to the PR and chat; round and loop limits →
   escalate.
8. Hotfix workflow + backport (cherry-pick, conflict → fix round) + templates.
9. BFF endpoints (`…/fix`, `…/monitor`, `/api/hotfixes`) + UI actions + the Fix rounds tab.
10. Chat commands.

## Exit criteria (the demo)

- On an agentd PR, a reviewer leaves 3 comments (2 actionable, 1 question). After a 5-minute quiet
  period:
  - **one** fix round pushes a commit;
  - the 2 actionable threads are replied to "Fixed in …" and resolved;
  - the question gets an answer and stays active;
  - the round report is posted.
- A red CI build (a failing test) → the monitor opens a fix round with the extracted error → pushes
  → CI goes green.
- A merge conflict → the target is merged into the source, resolved and pushed, with **no
  force-push** anywhere in the git history.
- On a **human PR**, a comment without `@agentd fix` does nothing, and one with it from a trusted
  reviewer triggers a round. The same comment from an untrusted user does nothing.
- **Create hotfix** on `release/2026.9` → plan gate → PR into release. After it is merged, a
  backport PR to `main` appears.

## Risks / open questions for review

- **Default scope:** `agentd` only (proposed), or `opted-in`?
- **Debounce time and max rounds:** 5 minutes and 5 rounds are proposed.
- **Release branch naming** for hotfixes (`release/*`?) and the backport target (`main`?).
- **Branch policies:** agentd's identity must be allowed to push to PR branches but **not** bypass
  policies. Confirm the ADO permissions.
