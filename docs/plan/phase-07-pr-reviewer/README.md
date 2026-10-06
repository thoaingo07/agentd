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

## Phase 7a — `!review` in chat (requested 2026-10-06, built before the rest of Phase 7)

A smaller first step that Phase 7 builds on: review **any open PR of a registered repository** from chat, talk the
findings through, and post only what the developer chooses.

**Decisions (2026-10-06):**
- **Thread first:** findings stay in the Discord thread until someone chooses. **1** post all to the PR · **2** keep in
  chat only · **3** discard, or `post 1,3` / `drop 2`.
- **Any PR in a registered repo**, by anyone in the channel. agentd only posts comments and **never votes or approves**.
- **Keep talking before posting:** follow-ups ("why is #3 a problem?", "#2 is intended") go to the same session. When
  something changes, the agent sends a revised findings list.
- **Model and effort:** `!review <pr> --model opus --effort high`, and `!model` / `!effort` in the thread, like ideas.

**Flow:** `!review <PR url or id> [--repo r] [--focus security,tests] [--model m] [--effort e]` → a 🔍 thread
"Review: PR !123 <title>" → a **read-only detached checkout of the PR head**, with the same read-only tools and no MCP as
brainstorms → the agent ends with a fenced `review-findings` block (`{summary, findings: [{severity, file, line, title,
detail, suggestion}]}`). Severities are blocker, major, minor and nit, with at most 30 findings. agentd shows the
findings numbered and asks for a choice.

**Posting:** one PR thread per finding, at its file and line on the PR's side (or PR-wide), plus a summary thread. Each is
prefixed with agentd's marker, so agentd's own comment handling skips them. The thread ids are stored.

**Delivery:**
1. **Foundations** (this PR):
   - `IPullRequestService.GetAsync` (title, author, branches, head commit, draft) and `CreateThreadAsync` (file and
     line anchored);
   - `IWorktreeManager.CheckoutCommitAsync`: fetch, then a detached checkout at the PR head. A later run moves the
     checkout to the new head, and only real commit ids reach git;
   - `pr_reviews` and `pr_review_messages` with their routines, and `IReviewStore`. Events carry ids and status only;
   - `ReviewFindings` (parse, validate, render);
   - the reviewer instructions (`ThreadTurnKind.Review` on the read-only thread agent).
2. **The chat flow** (built 2026-10-06):
   - **`!review <PR url | !id | id> [instructions…] [--repo r] [--focus f] [--model m] [--effort e]`:** the first word is
     the PR, and **anything else is instructions for the reviewer** (requested 2026-10-06). A PR URL names its
     repository; a bare id uses `--repo` or the only registered repository. Completed or abandoned PRs are refused.
   - **The 🔍 thread:** every message resumes the same review session. Each turn checks the PR head. After new pushes it
     moves the checkout and tells the agent.
   - **Choices on the findings:** **1** / `post` (all), `post 1,3`, `drop 2` (renumbered), **2** / `keep`, **3** /
     `discard`. Any other text is a question for the agent, which sends a revised block when its findings change.
   - **Posting:** one PR thread per finding at file:line, then a summary thread. It reports how many were posted, or
     where it stopped. No vote. Then the close-out question.
   - **`!model` / `!effort`** in a review thread apply from the next reply.
   - **Wiring:** the Discord poller reads review threads, and inbound messages route to `ReviewService`. `!help` lists
     `review`.
   - **The reviewer prompt** (`ClaudeBrainstormAgent.ReviewRules`, revised 2026-10-06):
     1. **Intent first:** check the change against the linked work items' acceptance criteria, flagging unmet criteria and
        out-of-scope changes.
     2. **Read the diff in context,** plus the repo's AGENTS.md, CLAUDE.md or CONTRIBUTING.md.
     3. **Look for, in order:** correctness, security (including secrets in the diff), breaking changes (APIs, contracts,
        configuration keys, migrations without a safe rollback), error handling, tests, performance, and consistency with
        the repo.
     4. **Verify before claiming,** and say in the finding what was checked.
     5. **Skip** formatter-level style, generated files, lockfiles, vendored code, and anything already raised in the open
        threads.

     Severity definitions: blocker = must fix before merge; major = should fix in this PR; minor = worth fixing; nit =
     optional. The agent replies in the developer's language.
   - **Context in the first prompt:** the linked work items (up to 3) with their acceptance criteria and description,
     read from `…/pullrequests/{id}/workitems`, and the open human comment threads (up to 20, resolved ones left out).
3. **Later:** reviews in the Web UI; then the rest of Phase 7 (reviewer catalog, dashboard, auto triggers).

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
