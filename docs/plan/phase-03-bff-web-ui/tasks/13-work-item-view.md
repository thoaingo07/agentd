# T3.13 — Work item view: one timeline across jobs

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 3 | T3.2, T3.4, T3.7, T3.10 | M | Agentd.Migrator, Agentd.Application, Agentd.Bff, Agentd.Web |

## Goal
Show the **whole life of a work item** on one page ([UI §4.3a](../../../ui/README.md)). A work item
like #5613 spans a first run, a rework, review fix rounds, a knowledge hand-off and a close-out, over
several jobs and PRs, and its chat thread may already be deleted.

## Files
- `src/Agentd.Migrator/Routines/event/event_list_by_work_item.sql`: create.
  - Events of every job of a work item, ordered by `(created, seq)`, keyset-paged (`after`/`before`).
- `src/Agentd.Migrator/Routines/messaging/conversation_history.sql`: create.
  - The outbox (what was posted, with delivery status) and inbound messages for a work item's
    conversations, in time order.
- `src/Agentd.Application/WorkItems/GetWorkItem.cs`, `GetWorkItemTimeline.cs`: create (queries).
- `src/Agentd.Application/Messaging/ChatCommands.cs`: modify. Record each command as a `chat.command`
  event (name, args, user).
- `src/Agentd.Bff/WorkItems/WorkItemEndpoints.cs`: create.
  - `GET /api/workitems/{id}`: summary, jobs, PRs.
  - `GET /api/workitems/{id}/timeline?after|before`.
- `src/Agentd.Web/ClientApps/dashboard/views/WorkItemView.vue` + `stores/workItems.ts`: create.
- `tests/…`: routine integration tests, BFF tests, component tests (vitest).

## Implementation
1. **Timeline entries** are a discriminated union shaped for the screen:
   - steps (domain events and `phase.set`);
   - chat (outbox, inbound and `DeveloperReplied`);
   - agent turns (`agent.*` grouped per turn);
   - PR events.

   Each entry carries the job id, so the UI renders a section per job.
2. **Conversation:** the outbox rows render as agentd's side, with delivery status (sent, failed,
   dead). Developer replies come from `DeveloperReplied` (they carry the text); commands come from
   `chat.command`. Mirrored copies show their source provider.
3. **Plan and usage:** from the job's `plan_estimate` and the `agent.rate_limit` events (utilization
   over time).
4. **Live:** the store subscribes to the hub for each active job of the work item and appends new
   entries; replay uses the same components.
5. **Links:** the History rows' work item cell and the Session header link here.

## Tests
- Routine: events from three jobs of one work item come back in order, and paging is stable in
  both directions.
- BFF: the summary lists jobs and PRs; the timeline merges events and chat; antiforgery and auth
  apply as on the other endpoints.
- UI: sections per job, the conversation shows both directions with delivery status, a tool call
  expands, and the plan shows actual against estimate.

## Done when
- [x] #5613's page shows all three jobs, both PRs, the hand-off and the close-out, with the full
  conversation, even though its thread is deleted.

## As built
T3.13 is split in two PRs, so each stays under 1,000 lines.

**T3.13a: backend (this PR)**
- **Routines** (`Routines/workitem/workitem_history.sql`):
  - `job_list_by_work_item`: oldest first.
  - `event_list_by_work_item`: keyset by `seq`; `after` ascending, `before` newest first, then
    reversed. Since T3.4, `seq` order is commit order, so `(seq)` is enough and no `(created, seq)`
    key is needed.
  - `outbox_list_by_work_item`: every provider, with delivery status. Heartbeats
    (`replaceStatusMessage`) are left out.
  - The planned file names are merged into one file, and there's no separate inbound-message
    routine: inbound rows don't store text. Replies come from `DeveloperReplied`, as planned.
- **Migration** `202610090001_work_item_history`: `ix_jobs_work_item (work_item_id, created_at)` and
  `ix_outbound_job (job_id, created_at)`.
- **Port and store:** `IWorkItemHistory` (`ListJobsAsync`, `ReadEventsAsync`, `ListPostedAsync`)
  with `WorkItemHistoryStore`.
- **Queries:** in `Application/Queries/WorkItemQueries.cs`.
  - `GetWorkItem`: jobs, the distinct PRs (one per job URL: the code PR and the knowledge sync PR),
    and every conversation link, open or closed, plus the first-seen and last-activity times.
  - `GetWorkItemTimeline`: event pages with `hasMore`.
  - `GetWorkItemConversation`: posted messages, plus `DeveloperReplied` and `chat.command` events,
    merged in time order. At most 2,000 posted messages; it scans up to 50,000 events for replies.
- **Endpoints:** `GET /api/workitems/{id}` (404 when agentd has no job for it),
  `/api/workitems/{id}/timeline?after|before&limit`, and `/api/workitems/{id}/conversation`.
  - The conversation has its own endpoint instead of being interleaved in the timeline. Chat lines
    and events are paged differently, and the UI merges them per tab.
  - In the view model, the writer of a reply is `author`, because `From` is the factory method.
- **`chat.command` events:** `ChatCommands` records each command typed in a job's thread (`name`,
  `args`, `text`, `user`, `provider`) before running it. A failure to record is logged and doesn't
  stop the command. Commands outside a thread have no job, and aren't recorded.
- **Tests:**
  - Infrastructure: events of three jobs of one work item (plus another work item's) come back in
    order, and paging is stable both ways; posted messages keep their delivery status, and
    heartbeats are skipped.
  - Application: the conversation merges both directions in time order (provider, error, author);
    timeline `hasMore`; a command in a thread is recorded.
  - Bff: summary, conversation, 404, and `after`+`before` → 400.
- **Checked on the demo database:** `/api/workitems/5613` shows jobs 1–3 (Done), PRs #3935 and
  #3936, and both Discord threads (closed: the thread was deleted). The conversation has 33 entries
  with the 5 replies, ending with the hand-off and close-out.

**T3.13b: the screen**
- **Route:** `/workitems/:id`. The WI label in every job table (Dashboard, History) and in the
  Session header links here.
- **`stores/workItems.ts`:** `open(id)` loads the summary, the first timeline page (read from the
  beginning, with "Load more" forward), the conversation, and each job's detail.
  - It subscribes the hub for each active job from the timeline's newest seq. The connection store
    now routes job-stream events to this store as well as the events store.
  - Live events are appended (deduplicated by seq). A non-agent event refreshes the conversation 2 s
    later, because chat posts aren't events.
- **Header:** state, WI and title, repo, number of runs, time from first pick-up to last activity,
  phase, links (work item, each PR), and each thread: a link while open, "thread closed" once it's
  closed or deleted.
- **Tabs** (`components/workitem/`):
  - **Timeline:** a section per job, labelled "Run 1", "Run n (rework)", plus "· hand-off" when the
    run carried the hand-off. Steps, questions, replies and errors reuse the Session view's rows;
    agent output is left to Activity.
  - **Conversation:** agentd's posts (Markdown, provider, delivery-status badge with the error as a
    tooltip), then replies and commands (author, provider). The message box targets the active job,
    if any.
  - **Activity:** each run's agent text and tool cards, plus a link to the full transcript.
  - **Pull requests:** each PR with the run that opened it and that run's review fix rounds. Review
    threads per PR come with the PR reviewer (Phase 7).
  - **Plan & usage:** per run, plan status, actual time against the estimate, peak 5-hour usage
    against the estimate, and weekly usage, from `agent.rate_limit` readings (listed under a
    disclosure).
- **Tests (`tests/workitem.spec.ts`):**
  - sections and labels, usage readings;
  - the Timeline has a section per job without agent output;
  - the Conversation shows both directions, status badge and error;
  - Plan shows the estimate against actual;
  - the store loads the story, subscribes the active job from the newest seq, and appends live
    events once.
- **Smoke test** on the demo database (`/workitems/5613`):
  - header: PR 3935 and PR 3936, and both Discord threads "closed";
  - timeline: Run 1 / Run 2 (rework) / Run 3 (rework) · hand-off with their steps;
  - conversation: 28 posts and the 5 replies, ending with the close-out question;
  - plan: each run's actual time; pull requests: each PR with its run;
  - no console errors.

  This meets the "Done when" item.
