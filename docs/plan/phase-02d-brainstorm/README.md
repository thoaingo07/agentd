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

## As built

**PR 1a: foundations**
- Tables `ideas` (repo, title, author, thread, status, Claude session, **model**, **effort**,
  checkout, drafts, created work items) and `idea_messages` (the conversation, both directions;
  it outlives the thread), with their routines and `IdeaStore`.
- **`ClaudeBrainstormAgent`:** Claude Code with the read-only tools minus agentd's MCP tools,
  `--disallowedTools` edits, and `--strict-mcp-config` with no MCP config (no agentd tools, no
  claude.ai connectors), plus `--model` / `--effort` from the idea. The reply is the session's
  final message; the transcript goes to `logs/idea-<id>/`.
- **`IWorktreeManager.CheckoutDetachedAsync`:** a detached checkout of the latest base branch at
  `worktrees/<repo>/idea-<id>`, reused if it's there.
- **`WorkItemDrafts`:** the agent proposes work items with a fenced ` ```work-items ` JSON block
  (ideas have no agentd MCP session, so there's no `propose_work_items` tool).
  - It's validated: User Story or Task, a title, a parent that's a story in the list, at most 10
    items.
  - It's rendered as stories with their tasks.
- **`BrainstormSettings`:** parses `--repo` / `--model` / `--effort`. Model is an alias or a full
  name; effort is `low` … `max`. Model and effort were requested on 2026-10-05.
- **Checked live** with CLI 2.1.289 and these exact arguments (`--model sonnet --effort low`):
  exit 0, the requested model, **no MCP servers**, and a valid `work-items` block.

**PR 1b: the chat flow**
- **`!idea [--repo r] [--model m] [--effort e] <text>`** in the channel.
  - It opens a thread named "💡 Idea: <first words>" with an opening message, stores the idea, and
    starts the first turn.
  - With one registered repository it's implied. With several, `--repo` is required (the open
    question is settled as "ask").
- **Every message in the thread resumes the same session** (`IdeaService`, a singleton).
  - One turn at a time per idea: messages that arrive during a turn are answered together next,
    with a "still thinking" note.
  - At most 2 ideas think at once, separately from the jobs' `MaxConcurrent`, so a running job
    doesn't block brainstorming.
- **Replies go straight to the thread** through the provider, not the job outbox (an idea has no
  job). The thread name comes from a new `ConversationSpec.Name`.
- **Drafts:** a valid `work-items` block marks the idea Proposed and is shown as stories with their
  tasks. A broken block is sent back to the agent to fix.
- **`!model <name>` / `!effort <level>`** in the idea's thread apply from the next reply; with no
  argument they show the current values.
- **Finished ideas** (closed, discarded, created) refuse new messages and point to `!idea`.
- **The usage limit or a failed turn** is reported in the thread; send the message again later.
- **Not done yet:** heartbeat and instant status replies for ideas.
**PR 2: create in Azure DevOps, close-out**
- **The drafts message offers choices:** **1** ✅ Create · **2** 🚀 Create and start · **3** ✏️ Change
  · **4** 🗑 Discard. People answer with the number, the label or a word. Any other text goes to the
  agent to revise.
- **`IWorkItemSource.CreateAsync`:** a JSON-patch `POST …/_apis/wit/workitems/$<type>` with:
  - title;
  - description and acceptance criteria as escaped simple HTML (paragraphs, `- ` lists; never raw
    HTML);
  - tags;
  - story points for stories, original estimate and remaining work in hours for tasks;
  - the repository's first area path;
  - a parent link (`System.LinkTypes.Hierarchy-Reverse`).

  It's created in the configured organization and project, with the operator's `az login`.
- **Order:** stories first, then their tasks linked to them. Each description ends with "From agentd
  idea #N, brainstormed with <author>".
- **Create and start** also tags the **stories** (and parentless tasks) with `ai-workflow` and the
  repository's match tag (`repo:<name>`), so the normal polling picks them up. Each becomes its own
  job thread, with plan approval on. Tasks stay under their story's job.
- **After creating,** the links are posted, the idea becomes Created (with the work item ids), and
  the close-out is asked: **1** delete the thread · **2** keep it (archived). If deleting fails (no
  Manage Threads permission), the thread is archived with a note. The conversation stays in
  agentd's database either way.
- **Discard** marks the idea Discarded, creates nothing, and asks the same close-out.
- **A failure partway through** reports which items were created and where it stopped.
