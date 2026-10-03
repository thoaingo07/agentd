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
- [ ] #5613's page shows all three jobs, both PRs, the hand-off and the close-out, with the full
  conversation, even though its thread is deleted.
