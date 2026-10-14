# agentd: the Inbox

> Decided 2026-10-14: every message agentd sends a person also lands in one Inbox in the web UI, whatever chat is set
> up. With Discord on, the Inbox is a second place to read and answer; with chat off, it's how agentd talks to you.
> Modeled on [Plannotator's Inbox](https://plannotator.ai/inbox/): threads grouped by what they need from you.

## 1. What's in it

One **thread** per source: a job (its work item), an idea, a chat `!review`, a chat, a watched PR. A thread holds
agentd's messages to people, in order: questions, plans to approve, results, failures, notices. Not progress updates
(they're replaced as they go) and not people's own messages (those stay on the source's page).

| Source | Written by | Recipient |
|---|---|---|
| `job` | `agentd.outbox_enqueue`, so every job message, in the same transaction as the state change that caused it, with or without a chat thread | none yet (the work item's assignee later) |
| `idea` | `IdeaService.PostAsync` | the idea's author |
| `review` | `ReviewService.PostAsync` | the review's author |
| `chat` | `ChatService` | the chat's author |
| `watch` | `PrWatchNotices.PostAsync` | who watched the PR |

## 2. What each thread needs

A thread is **waiting on you** while its source is, read live from the source (never guessed from messages):

- a job **WaitingForHuman** (a question, the plan, the hand-off, the close-out) or with a tool **permission** pending;
- an idea with work items **Proposed**, or Created/Discarded with a chat thread still to close;
- a review **Reviewed** (post, keep or discard), or Kept/Discarded with its close-out open;
- a watched PR with a fix **pending** (push or discard).

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

## 4. The page (next)

`/inbox`: the grouped list on the left, the open thread on the right with its messages and, when it waits on you, the
question's buttons and a reply box (sent to the source like its page or chat thread would). **Mine** shows threads
addressed to you, **Everyone** all of them (every signed-in user, like jobs and reviews). Browser notifications for
threads that start waiting on you.
