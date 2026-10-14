# agentd: the Inbox

> Decided 2026-10-14: every message agentd sends a person also lands in one Inbox in the web UI, whatever chat is set
> up. With Discord on, the Inbox is a second place to read and answer; with chat off, it's how agentd talks to you.
> Modeled on [Plannotator's Inbox](https://plannotator.ai/inbox/): threads grouped by what they need from you.

## 1. What's in it

One **thread** per source: a job (its work item), an idea, a chat `!review`, a chat (and a watched PR's, from before the PR Monitor was turned off: readable, no answers). A thread holds
agentd's messages to people, in order: questions, plans to approve, results, failures, notices. Not progress updates
(they're replaced as they go) and not people's own messages (those stay on the source's page).

| Source | Written by | Recipient |
|---|---|---|
| `job` | `agentd.outbox_enqueue`, so every job message, in the same transaction as the state change that caused it, with or without a chat thread | none yet (the work item's assignee later) |
| `idea` | `IdeaService.PostAsync` | the idea's author |
| `review` | `ReviewService.PostAsync` | the review's author |
| `chat` | `ChatService` | the chat's author |
| `watch` | nothing now (the PR Monitor is turned off; old threads stay) | who watched the PR |
| `incident` | `IncidentMonitor` (an alert for a new or reopened incident, resolved), `IncidentInvestigator` (the analysis with 🛠 Fix it / 🙈 Ignore, answers), `IncidentService` (Bug filed, ignored, reopened) | none (everyone) |

## 2. What each thread needs

A thread is **waiting on you** while its source is, read live from the source (never guessed from messages):

- a job **WaitingForHuman** (a question, the plan, the hand-off, the close-out) or with a tool **permission** pending;
- an idea with work items **Proposed**, or Created/Discarded with a chat thread still to close;
- a review **Reviewed** (post, keep or discard), or Kept/Discarded with its close-out open;
- an incident **Open** with an analysis and no Bug filed (🛠 Fix it or 🙈 Ignore);

The list, newest activity first within each group:

1. **Waiting on you**: waiting threads you haven't marked read since agentd last wrote.
2. **New**: threads with messages you haven't read.
3. **Earlier**: the rest.

**Read** is per person: opening a thread, or **Mark read** (`e`), marks it read up to its latest message; a waiting
thread then drops out of the top until agentd writes again. The nav shows how many threads wait on you.

## 3. Storage

- `agentd.inbox_items`: `id`, `source`, `source_id`, `title` (the thread's name when it was written), `recipient`,
  `kind` (`Info`, `Question`, `Result`, `Error`, …), `markdown`, `options` (a question's buttons), `created_at`.
- `agentd.inbox_reads`: per person and thread, read up to which item.
- Routines: `inbox_add` (also an `inbox.added` event, for live updates), `inbox_threads` (the grouped list for a
  person), `inbox_thread` (one thread's messages), `inbox_mark_read`, `inbox_counts`, `inbox_waiting` (the live
  state above), `inbox_delete_before` (retention, `Agentd:Inbox:RetentionDays`, 90).
- **Never fails a post:** an Inbox write that fails is logged and skipped, like the prompt log.

## 4. The page

`/inbox` (and `/inbox/{source}/{id}` for an open thread): the grouped list on the left (resizable), the open thread on
the right with its messages, oldest first, and, when it waits on you, the question's buttons (formerly a watched PR's **push** /
**discard**) and a reply box. The open thread keeps its group while it's open, so reading it doesn't move it. **Mine**
shows threads addressed to you (the recipient is your web login, so for now only what you started from the web),
**Everyone** all of them (every signed-in user, like jobs and reviews). `j` / `k` move between threads, `e` marks the
open one read. The nav's **Inbox** item shows how many threads wait on you (or, when none do, how many are unread);
`inbox.added` and job, idea and review events refresh it.

API (every signed-in user):

- `GET /api/inbox?view=all|mine&limit=` → `{ threads, counts }`; `GET /api/inbox/counts`.
- `GET /api/inbox/{source}/{id}?before=` → the thread's messages, newest first.
- `POST /api/inbox/{source}/{id}/read {until}`: marks it read for you.
- `POST /api/inbox/{source}/{id}/reply {text}`: to the source, as its page or chat thread takes it: a job through
  `JobMessages` (like its page's message box, mirrored to its chat threads), an idea or chat like a message from the
  web, a chat `!review` like a message in its thread (not mirrored to the thread yet).
  An incident through `IncidentService` (🛠 Fix it, 🙈 Ignore, reopen, investigate again for Operators and Admins;
  anything else is a question for its agent). A watched PR's old thread answers that watching is turned off.

**Notifications** (added 2026-10-14): **Enable notifications** in the Inbox's header turns on browser notifications
for this browser (the browser asks once; the choice is kept as `agentd.notify`). While agentd is open in a tab, a
thread that starts waiting on you raises one, named after the thread with its question; clicking it opens the
thread. Never for what was already waiting when the page opened. The tab's title counts the threads waiting on you
(`(2) agentd`), notifications on or not. A blocked site says so instead.

Next: recipients matched across chat and web identities.
