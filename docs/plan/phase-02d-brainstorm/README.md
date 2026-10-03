# Phase 2d — Brainstorm ideas into work items

**Goal:** turn an idea into well-formed Azure DevOps work items by brainstorming it with the agent
in a chat thread, grounded in the real code, and optionally start the normal job lifecycle
([Phase 2b](../phase-02b-job-lifecycle/README.md)) on them right away.

Requested on 2026-10-03. Decisions:
- **Types:** User Story + Task (the agent picks).
- **Where:** Discord first; a Web UI **Ideas** page later reads the same history.
- **When:** planned now, built **after Phase 3**, so ideas get a page from day one.

---

## Flow

```
!idea <text> [--repo <name>]  →  💡 thread + read-only brainstorm session
        ↓  (every message resumes the same session: questions, options, trade-offs, code pointers)
🗂 propose_work_items  →  1. ✅ Create · 2. 🚀 Create and start · 3. ✏️ Change · 4. 🗑 Discard
        ↓ change → revise ↺
✅ created in Azure DevOps (links posted)   🚀 + ai-workflow, repo:<name> → each starts its own job thread
        ↓
🧹 close-out: "Delete this thread? 1. 🗑 · 2. 📦" (the transcript stays in agentd's database)
```

### 1. Start
- **`!idea <text>`** in the parent channel. `--repo <name>` picks the repository; with only one
  registered repository it is implied, and with several and no flag the agent asks.
- agentd opens a thread **"💡 Idea: <first words>"** and starts a Claude session in **brainstorm mode**:
  - a **detached, read-only checkout** of the repository's base branch (its own worktree path per
    idea: `~/.agentd/worktrees/<repo>/idea-<id>`), so ideas are grounded in the code;
  - **read-only tools** (the plan gate's `ReadOnlyTools`): no edits, commits, branches or PRs;
  - the same **heartbeat**, **instant status replies** and **usage warnings** as jobs.

### 2. Brainstorm
- Every message in the thread resumes the **same session** (one session per idea, on this machine;
  see [deployment.md §8.1](../../architect/deployment.md)).
- The agent clarifies the goal, explores options and trade-offs, points at the code that would
  change, and sizes the work roughly. It uses `set_phase` (`clarify`, `plan`) so the steps show.

### 3. Propose work items
- The agent calls a new MCP tool **`propose_work_items(items)`**. Each item has:
  - `type` (User Story or Task) and `title`;
  - `description` and `acceptanceCriteria`;
  - `estimate` (story points, or hours for tasks) and `tags`;
  - `parent` (a task under a story, by its index in the list).
- agentd posts the drafts and asks: **1. ✅ Create · 2. 🚀 Create and start · 3. ✏️ Change · 4. 🗑 Discard**.
  - **Change** (or any other feedback): the agent revises and proposes again.
  - **Create:** agentd creates the items in Azure DevOps (`System.Title`, `System.Description`,
    `Microsoft.VSTS.Common.AcceptanceCriteria`, `System.Tags`, the area path of the repository's match
    rule, parent/child links) and posts their links.
  - **Create and start:** as Create, plus the tags `ai-workflow` and `repo:<name>` on the stories (and
    on tasks without a parent), so the normal polling picks them up. Each becomes a job in its own
    work item thread, with plan approval on by default.
  - **Discard:** nothing is created.
- Writes to Azure DevOps use the operator's `az login`, like comments and PRs today.

### 4. Close-out
- The same question as jobs: "🧹 Delete this thread? 1. 🗑 Delete thread · 2. 📦 Keep (archive)".
- The idea, its transcript and the created work item ids stay in the database, so the work item's
  page can show "born from idea #N" with the whole discussion.

---

## Model
- **`Idea` aggregate** (separate from `Job`, because there is no work item yet): id, repository,
  title, status (`Brainstorming → Proposed → Created | Discarded → Closed`), Claude session, worktree,
  drafts (jsonb), created work item ids, timestamps, version. Its conversation reuses `conversations`
  (a nullable `idea_id` next to `job_id`).
- **Events:** `IdeaStarted`, `WorkItemsProposed`, `WorkItemsCreated`, `IdeaDiscarded`, `IdeaClosed`,
  in `agentd.events` (an `idea_id` column), so the timeline and the future Ideas page can read them.
- **The dispatcher** runs idea turns under the same `MaxConcurrent` limit as jobs, with the same
  runner, in read-only mode.
- **Azure DevOps:** `IWorkItemSource.CreateAsync(type, fields, parentId?)`, the one new write.

## Delivery (each PR under 1,000 lines of code)
1. **Idea model and session:** migration (`ideas`, `idea_id` on `conversations`/`events`), `!idea`,
   the read-only detached checkout, brainstorm turns through the dispatcher, the thread, heartbeat
   and status.
2. **Proposals and creation:** `propose_work_items`, revise/create/start/discard, the ADO
   work item create with parent links and tags, links posted, close-out.
3. **Web UI Ideas page** (after Phase 3): the list of ideas and an idea's timeline and conversation,
   linked from the work items it created.

## Exit criteria (the demo)
- `!idea` in #agents opens a 💡 thread; a few messages of back-and-forth reference real files.
- The agent proposes a User Story with two Tasks; after one "change" round, **🚀 Create and start**
  creates them in `ermsystem/Portal`, linked parent→child and tagged. The story's job opens its own
  thread and posts its plan for approval.
- The idea thread asks to be deleted and is deleted on "1"; the idea's history is still in the database.

## Open questions
- **Default repository** when several are registered and no `--repo` is given: ask, or use the
  first one?
- **Idea timeout:** close an idle brainstorm after N days (proposed: 7, with reminders like the
  question timeout)?
