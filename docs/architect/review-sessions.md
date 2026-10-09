# agentd: review sessions (web UI and `agentd review`)

> Decided 2026-10-09. One review experience, two places it runs:
> - **on the agentd server**, in the web UI (and chat): any branch, PR or commit range of a registered repository,
>   pulled with agentd's credentials and reviewed by the server's Claude;
> - **on a developer's laptop**, with `agentd review`: their uncommitted (or unpushed) changes, reviewed by **their
>   own `claude`**, on a page the CLI serves on localhost. The server isn't involved unless they `--share`.
>
> Both show the same page: the diff, agentd's findings inline (keep, edit or drop), your comments, Ask (explain this),
> and **Send**. Monitors (the PR Monitor, the system monitor) later open review sessions for the fixes they propose.

---

## 1. What a review session is

| Field | |
|---|---|
| `id` | |
| `target` | `pr` (repo + PR id), `branch` (repo + branch, against its base), `range` (repo + `base..head` commits), or `upload` (a diff sent by `agentd review --share`) |
| `base_commit`, `head_commit` | pinned when it starts, so the diff, the findings and the comments agree even if the branch moves |
| `status` | `Reviewing` → `Ready` → `Sent` (or `Closed`); `Failed` with a reason |
| `model`, `effort` | the reviewer's (`Jobs:Steps:review`, or the request's) |
| `summary`, `findings` | the reviewer's result: today's `ReviewFinding` (severity, file, line, title, detail, suggestion) plus the person's **decision**: `kept` (default), `dropped`, or `edited` with their text |
| `comments` | the person's own notes on a file and line (or the whole change) |
| `asks` | questions on a selection and their answers |
| `created_by`, `sent_to` | who started it; where Send delivered it (see §4) |

The diff itself isn't stored for server targets: it's recomputed from the pinned commits in the server's bare clone
(`git diff base...head`, capped at the same size as the job Diff tab). An **upload** stores its diff (and the changed
files' contents, for context), and is kept for 30 days.

`!review` on a PR keeps its chat flow (`pr_reviews`) and gets a review session alongside it, so the same review can be
worked in chat or on the page.

## 2. The page

Desktop: files on the left, the diff in the middle, findings and comments on the right. Phones: three tabs (Files ·
Diff · Findings), and the inline cards in the diff.

```text
┌ Reviews › sysmin · feature/keyset-chunks → develop ──────────────────────────── [ Send ▾ ] ┐
│ 12 files · +340 −120 · 🔴 2  🟠 1 · claude-opus-5-5 · high · a1b2c3d…f9e8d7c · Ready        │
├───────────────┬────────────────────────────────────────────────────┬──────────────────────┤
│ Files         │ src/Sync/BaseSyncHandler.cs                         │ Findings · 3         │
│ 🔴 BaseSync…  │   40    var page = 0;                               │ 🔴 1 Unbounded read  │
│ 🟠 Options.cs │   41  - var rows = await ReadAllAsync(table);       │    BaseSync…cs:41    │
│    Tests/…    │   41  + await foreach (var chunk in KeysetAsync(    │    Keep · Edit · Drop│
│    docs/…     │       ┌ 🔴 #1 Unbounded read of the whole table ───┐│ 🔴 2 Missing cancel… │
│               │       │ Large tables load into memory at once.     ││ 🟠 3 N+1 lookups     │
│               │       │ Fix: page by the primary key (keyset).     ││──────────────────────│
│               │       │                    [Keep] [Edit] [Drop] ✓  ││ Your comments · 1    │
│               │       └────────────────────────────────────────────┘│ 💬 Options.cs:12     │
│               │   58  + private const int ChunkSize = 5000;  💬     │    "make it config"  │
│               │                                                      │──────────────────────│
│               │  select lines → [💬 Comment] [❓ Ask]                │ ❓ Ask about the code │
└───────────────┴────────────────────────────────────────────────────┴──────────────────────┘
```

- **Findings** sit inline at their line and in the side list (worst first). Keep is the default; **Edit** rewrites the
  text you send; **Drop** removes it from Send (it stays visible, struck through).
- **Comment**: select lines (or a file, or nothing for the whole change) and write a note. It's sent with the kept
  findings.
- **Ask**: select code and ask ("why is this here?", "explain this flow"). The answer appears in the side panel and is
  kept with the session. On the server it has the repo and Azure DevOps context (like `!chat`); locally, your repo.
- **Live**: while the reviewer works the page says "Reviewing…" and checks the session every few seconds; the findings
  arrive together when the reviewer finishes (its review-findings block). The diff and comments work meanwhile.
  (Streaming findings one by one, over the SignalR hub, can come later if reviews get long.)
- **Send ▾** lists the destinations for where the session runs (§4), with a preview of the exact text that goes.
- Keyboard: `j`/`k` next/previous finding, `c` comment, `a` ask, `x` drop, `e` edit.

The same Vue app (`ClientApps/review`) serves both places; only its API base differs (`/api/reviews/<id>` on the
server, the CLI's `/api/review` locally).

## 3. On the server

**Start** from:
- **Web UI → Reviews → New review**: pick a repository, then a PR, a branch (against its base, or a branch you
  choose), or two commits;
- **chat**: `!review !3944` (today) or `!review branch:feature-x [--base develop]`;
- **a job's page**: "Review this branch" (its work so far).

**The pipeline:**
1. `git fetch` in the repository's bare clone (agentd's SSH key), resolve and pin `base` and `head`.
2. A read-only checkout of `head` (`worktrees/<repo>/review-<id>`), as `!review` does today.
3. The reviewer turn: the server's Claude with `ReviewRules`, the diff, and the repo's `AGENTS.md` / `.agentd/`
   rules. Findings arrive as JSON (today's format) and stream into the session.
4. `Ready`. The checkout stays until the session is Sent or Closed (Ask uses it), then it's removed.

At most two reviews run at once (`ReviewSessionReviewer.MaxConcurrent`). A session left `Reviewing` by a restart is
started again by the review monitor's next pass. A usage limit, an unreadable reply or an error makes it `Failed`
with the reason shown on the page.

Who may start one: anyone signed in to the web UI (or allowed in chat), for registered repositories.

## 4. Send: where the feedback goes

| Session | Destinations |
|---|---|
| a **PR** | **Post to the PR**: the kept findings and comments as PR threads plus the main message (today's `!review` posting), under **your name** when you've connected your Azure DevOps (`ado-user-delegation.md`); **Fix it**: an agentd job that pushes fixes to the PR's branch |
| a **branch** or **range** | **Fix it**: an agentd job on that branch (a fix PR into it, or pushes when it's an agentd branch); **Copy as text** |
| an **agentd job's branch** | **Send to the job's agent**: a fix round in its session (today's flow for PR comments) |
| an **upload** (`--share`) | **Back to my laptop**: the CLI that shared it picks it up (§5); **Copy as text** |

The text sent is the same everywhere: the kept and edited findings (worst first, with file:line and the fix), then
the comments, each with its location.

## 5. `agentd review` on a laptop (the developer's own Claude)

```bash
agentd review                 # uncommitted changes (staged + unstaged), against HEAD
agentd review --base develop  # everything since develop: commits on the branch + uncommitted
agentd review --fix           # after Send, let my claude fix it, then review the result again
agentd review --share         # also put the session on the agentd server (needs `agentd login`)
```

1. **Collect**: `git diff` against the base (untracked files included with `--untracked`), and the changed files'
   contents for context. Nothing leaves the laptop.
2. **Review with your `claude`**: the CLI runs your installed Claude Code in the repository, read-only:
   `claude -p "<review prompt + diff>" --output-format json --permission-mode plan --allowedTools Read,Grep,Glob,Bash(git log:*),Bash(git show:*)`,
   with the same `ReviewRules` and the repo's `AGENTS.md` / `.agentd/` rules. It uses **your** login and
   subscription. Findings come back as the same JSON.
3. **Serve the page**: an in-process Kestrel on `127.0.0.1` and a random port, with a one-time token in the URL, and
   the same CSP as the web UI. It opens your browser; `--no-open` prints the URL. The session lives in
   `.agentd/reviews/<id>.json` while it runs.
4. **Ask** runs your `claude` too (read-only, in the repo).
5. **Send**:
   - **default**: the feedback is printed and written to `.agentd/review.md`; tell your agent "fix what's in
     `.agentd/review.md`". The CLI exits.
   - **`--fix`**: the CLI runs your `claude` in the repository with the feedback (`--permission-mode acceptEdits`,
     edits only, no commits or pushes), then re-collects the diff and reviews again: the page shows round 2. It warns
     first if the tree has changes it would mix with (it suggests a commit or stash).
6. **`--share`** uploads the session (the diff, the context files, the findings and comments; never the rest of the
   repo) so you can continue in the web UI on another device. Send from the web UI comes back to the waiting CLI
   (it long-polls the session) and is handled as above.

`agentd login <url>` (once) is only for `--share`. On the tailnet the server knows who you are (Tailscale identity),
so there's nothing to paste.

The CLI carries the page (the `review` app from Agentd.Web's build, embedded in the single-file binary) and the review
prompt, so a local review and a server review behave the same.

## 6. API

Server (`/api`, signed in, antiforgery on writes):

| | |
|---|---|
| `POST /api/reviews` | start: `{ repo, target: { pr } | { branch, base? } | { base, head } }` → the session |
| `GET /api/reviews` · `GET /api/reviews/{id}` | list (mine, recent) · one session: target, commits, summary, findings with decisions, comments, asks, status |
| `GET /api/reviews/{id}/diff` | the unified diff and file list (pinned commits) |
| `PUT /api/reviews/{id}/findings/{n}` | `{ decision: kept | dropped | edited, text? }` |
| `POST /api/reviews/{id}/comments` · `DELETE …/comments/{c}` | `{ file?, line?, endLine?, text }` |
| `POST /api/reviews/{id}/asks` | `{ file?, line?, endLine?, question }` → the answer (streamed on the hub) |
| `POST /api/reviews/{id}/send` | `{ destination }` → what happened |
| `POST /api/reviews/uploads` | `agentd review --share`: `{ repo?, base, diff, files[], findings, comments }` |

The local CLI serves the same shapes under `/api/review` (one session), so the page code is shared.

## 7. Storage

`agentd.review_sessions` (the fields in §1; findings, decisions and the summary as `jsonb`), `review_comments`,
`review_asks`, and `review_uploads` (the uploaded diff and files, with an expiry). Routines in `Routines/review/`
with integration tests, including parallel callers for decisions and comments on the same session.

## 8. Security

- Server: the same sign-in, roles and antiforgery as the rest of the web UI. Uploads are size-capped (diff and files)
  and expire.
- Local: bound to 127.0.0.1 only, a random port, a one-time token (cookie after the first load), no CORS. The
  reviewer and Ask run read-only; only `--fix` edits, and never commits or pushes.
- Neither path gives the reviewer any agentd secret: locally it's the developer's own Claude; on the server the
  reviewer's environment is the agents' (no daemon secrets), as today.

## 9. Delivery (PRs under 1000 lines each)

1. **Sessions on the server**: the schema and routines, `ReviewSessionService` (start, pin, diff, decisions,
   comments), the API without the reviewer.
2. **The reviewer and the page**: the reviewer turn streaming findings; `ClientApps/review` (files, diff, inline
   findings, keep/edit/drop, comments, live).
3. **Ask, chat entry and Reviews list**: Ask on a selection, `!review branch:…`, Web UI → Reviews.
4. **`agentd review`**: collect, your `claude` reviewer, the local server and the embedded page, Send to
   `.agentd/review.md`.
5. **`--fix` rounds and `--share`**.
6. **Send on the server**: Post to the PR (under your name: the on-behalf-of part 3b), Fix it jobs, Send to the
   job's agent.

The PR Monitor and the system monitor come after, opening sessions for the fixes they propose.
