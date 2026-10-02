# Reference: VirtusLab Tulip (PR explainer)

**Source:** https://github.com/VirtusLab/tulip (Apache-2.0, TypeScript). Reviewed 2026-10-02 as input
for the Phase 7 PR dashboard.

Tulip explains a GitHub PR as one web page. It groups the changes by concern, orders the groups by how
much attention they need, and writes a top-down explanation (prose, a diagram, then code) in which
**every changed line appears somewhere**. Like agentd, it drives the headless `claude` CLI on a
**Claude subscription** (no API key).

## How it works

| Pass | Model | What it does |
|---|---|---|
| 1. Fetch & check out | — | PR metadata + diff; detached checkout of the head; the diff and the *before* versions of changed files are written into the checkout so sessions can read real code. |
| 2. Categorize | Sonnet | Split changes into a few **self-contained groups by concern**. Tests and docs ride along with their code. There is never a "tests" or "docs" group. Each group gets an **attention** rating: *Read closely* / *Read through* / *Skim*. A review pass amends the split once. |
| 3. Split large changes | Sonnet | For changes over ~120 lines, the LLM proposes **split points** (never ranges); code builds an exact partition, so no line is lost. |
| 4. Classify | Haiku | Assign every change to group(s), production vs test; drop generated files and lockfiles; **coverage is checked in code**. A change in several groups is **owned** by its highest-attention group and only linked from the others. |
| 5. Explain | Opus (+ Sonnet review) | One session per group: overview and an orienting **Mermaid** diagram chosen by change kind (dependency, data flow, class, state, ER, sequence), then each change **signature-first**, routine code folded. Snippets are verified to exist; diagrams are verified to render. One review round, one amend. |
| 6. Render | — | One self-contained HTML page: light/dark, sidebar TOC, side-by-side highlighted diffs, attention badges. `--serve` adds a per-group comment box that posts to the PR. |

## What agentd should borrow

1. **An "Explain" view for `/prs/:repo/:id`.** Next to Reviews and Threads: categories ordered by
   attention, prose + diagram per category, and every change reachable. It's useful for a human reviewing
   an agentd PR, and for the PR Reviewer itself.
2. **Coverage guaranteed in code, not by the prompt.** Every change must land in a category; snippets and
   diagrams are validated before the page is shown. This fits agentd's "LLM proposes, code verifies" style.
3. **Partition by concern, rate by attention.** Attention orders the groups and labels them; it never splits
   a feature into "hard part" and "wiring". Reviewer findings (Phase 7) can be attached to these categories.
4. **The primary-owner ledger.** Explain a shared change once, and link to it from the other categories. This
   avoids re-narrating big test files.
5. **Model per pass.** A cheap model classifies, a mid model categorizes and reviews, a strong model explains.
   That maps directly onto agentd's model profiles (Phase 6).
6. **Few sequential calls.** One review round + one amend, parallel batches with a global process cap.
   Tulip found that extra review rounds rarely converge (their ADR 0025).
7. **Prompts as plain-text templates** in the repo. In agentd they belong in the kit (`.agentd/`), so
   teams can tune them.
8. **Read-only sessions on a detached checkout** (Read/Grep/Glob, no Bash). This matches the Phase 7 reviewer
   worktree.

## What differs for agentd

- **Azure DevOps, not GitHub:** the diff comes from the ADO PR iterations API, or from `git diff` in the
  managed bare clone (which agentd already has). Comments go to ADO threads.
- **Inside the Web UI, not a static file:** the explanation is stored per PR head commit and rendered by the
  Vue app under strict CSP. That rules out inline scripts. Mermaid renders client-side from a bundled module,
  and diagrams are validated server-side before storing.
- **Its own job kind** (`PrExplain`), sharing the Phase 7 review worktree and trace. It runs on demand from
  the dashboard, or automatically for PRs agentd opened.
- **No code is reused.** Tulip is TypeScript; agentd reimplements the passes as a MAF workflow (Phase 4/7).
  The ideas and prompt structure are the reference (Apache-2.0, credit in the prompts' headers if adapted).

## Open questions

- Should the Explain view be generated automatically for every agentd-opened PR (cost), or only on demand?
- Diagram rendering: bundle Mermaid in `Agentd.Web` (≈ 1 MB, lazy-loaded on the PR page only) or render SVG
  server-side.
