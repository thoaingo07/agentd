# Phase 7 — PR Reviewer + PR dashboard

**Goal:** review **any open PR** with the repo's **predefined reviewers** from `.agentd/reviewers/`,
started from a new **PR dashboard**, with findings posted as PR threads. The job workflow's Review
phase switches to the same reviewer catalog.

Design refs: [pr-reviewer-and-monitor.md §1, §3, §6](../../architect/pr-reviewer-and-monitor.md) ·
[UI §4.4](../../ui/README.md#44-pull-requests) · [ADO reference: PRs](../../architect/references/azure-devops.md)

---

## Scope

**In**

- **Kit v1.1:**
  - the `reviewers/` folder with `general`, `security`, `architecture`, `tests` and `performance`,
    each as frontmatter + instructions;
  - `templates/pr-review-summary.md`;
  - the `kit.json` `pr.review` and `workflow.review.reviewers` sections;
  - the reviewer file schema in `ValidateKit`.
- **Domain:** `PullRequest` (tracked), `ReviewRun`, `Finding` (+ fingerprint); `JobKind.PrReview`.
- **Application:**
  - `IReviewerCatalog`;
  - the `IPullRequestService` extensions (list active PRs, threads, reviewers and votes, policy
    status, merge status);
  - `SyncPullRequests` (read-only sync for the dashboard);
  - `StartPrReview`, `AggregateFindings`, `PostReview`.
- **The MAF review workflow:** prepare a detached worktree at the PR head → **fan out** the selected
  reviewers in parallel (read-only) → fan in → aggregate (dedupe, rank, threshold) → post or dry run.
- **Posting:**
  - one thread per finding, anchored to file and line, with a hidden fingerprint marker;
  - a summary comment;
  - agentd resolves its own threads that no longer reproduce, and respects `wontFix`;
  - `vote: none` by default, and agentd **never approves**.
- **Auto triggers** (`onCreate` + `autoOn` globs), once per head commit, only on authorized authors.
- **UI:**
  - the **`/prs` dashboard** (filters, needs attention, a row menu);
  - the **Run review** dialog (the reviewer picker from `GET /api/repos/{repo}/reviewers`, post or
    dry run, a model override);
  - **`/prs/:repo/:id`** (Reviews + Threads tabs, and a live trace);
  - the `pullRequests` store.
- **Chat:** `/review <pr> [reviewers]`.

- **PR explainer** (inspired by [VirtusLab Tulip](../../architect/references/tulip.md)): an **Explain**
  tab on `/prs/:repo/:id`.
  - Changes are grouped by concern and ordered by attention (*Read closely* / *Read through* / *Skim*).
  - Each group gets prose, a validated Mermaid diagram and its diffs. Every change is covered, which is
    checked in code.
  - It runs as a `PrExplain` job on the review worktree, on demand or automatically for PRs agentd opened.
  - Reviewer findings are shown inside the category they belong to.

**Out:** fixing (Phase 8), hotfix (Phase 8), `scope: all` monitoring (Phase 8).

---

## Tasks

Task index: [tasks/README.md](tasks/README.md) (the detail files are written when this phase starts).

1. Reviewer files in `kit/v1.1` + the schema; the catalog loader from the kit snapshot.
2. ADO client extensions + recorded fixtures (PR list, threads create/reply/patch, policy
   evaluations).
3. `pull_requests`, `review_runs` and `review_findings` tables; a sync worker (read-only) for the
   dashboard.
4. The review workflow (fan-out/fan-in) + the aggregator (fingerprint = file + normalized title +
   nearby-code hash).
5. The poster: threads + summary + reconciling with earlier runs.
6. Switch the job Review phase to `workflow.review.reviewers`.
7. BFF endpoints (`/api/prs…`, `/api/repos/{repo}/reviewers`, `POST …/reviews`) + role policies.
8. UI: PullRequestsView, the Run review modal, PullRequestView (Reviews / Threads / Trace).
9. The chat command.
10. The PR explainer: categorize → split large changes → classify (coverage checked in code) → explain
    per category (diagram validated) → stored per head commit → Explain tab.

## Exit criteria (the demo)

- `/prs` lists open PRs from two repos, with CI, votes, conflicts and active threads, updating live.
- On a **human-authored PR**, an Operator picks `security` + `tests` → the reviewers run in parallel
  → findings appear as threads on the right lines, plus a summary comment. A **dry run** shows the
  findings only in the UI.
- Re-running after the author fixes one issue resolves that thread and doesn't duplicate the others.
  A thread marked `wontFix` is not re-posted.
- A PR that touches `src/Agentd.Bff/**` triggers `security` automatically on creation.
- A job's Review phase now uses the kit's `workflow.review.reviewers`.
- The Explain tab of an agentd-opened PR shows attention-ordered categories with diagrams, and every
  changed line is reachable from it.

## Risks / open questions for review

- **The initial reviewer set and their instructions:** do you want to write or tune these first?
- **Noise control:** default `severityThreshold` and `maxFindings` per reviewer.
- **PR explainer cost:** generate automatically for every agentd-opened PR, or only on demand? Mermaid
  bundled client-side (lazy) or rendered server-side?
- **Auto-review default:** on creation only for agentd PRs, or for all PRs by authorized authors?
- **Identity:** agentd posts as the PAT or service-principal identity. Which ADO account should that be?
