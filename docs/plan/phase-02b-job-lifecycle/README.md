# Phase 2b — Full job lifecycle in chat (clarify → plan → implement → verify → PR → review loop → hand-off)

**Goal:** a job runs the whole lifecycle with the developer in its chat thread, posting a
notification at every critical step. It keeps fixing review comments until the PR is ready to
complete, then hands off: it extracts the knowledge and learnings, agrees with the developer how to
sync them, opens a sync PR, and closes out the thread.

Requested by the developer on 2026-10-03, after the first live run (#5613 → PR #3935). Two problems
in that run: a reply sent mid-turn was never seen ("confirm the plan first"), and the thread was
silent between "started" and "PR ready".

This phase brings parts of **Phase 4** (phases, plan gate), **Phase 8** (PR monitor, fix rounds) and
**Phase 9** (learning loop) forward, in a light form on today's single-session Claude runner. Those
phases build on what is delivered here instead of re-building it.

---

## Lifecycle

```
📥 Claimed → 🧠 Clarify spec ⏸(questions) → 📝 Plan ⏸(developer's OK) → 🛠 Implement → 🧪 Verify → 📤 PR opened
                                                                                                      │
   ┌──────────────────────────────────────────────────────────────────────────────────────────────────┘
   ▼
🔁 Review loop: PR comments / failed checks → agent fixes → push → reply on the PR threads
   │   (repeats until all threads are resolved and the checks pass)
   ▼
✅ Ready to complete → (developer completes the PR) → 🎓 Hand-off ⏸ → 🧹 Close-out → 🎉 Done
                                                              (PR abandoned → 🚫 Cancelled → 🧹 Close-out)
```

Every arrow posts to the work item's thread.

### Notifications
- **agentd's own steps (deterministic):**
  - claimed (title and link);
  - work item fetched (type, state, acceptance criteria);
  - worktree created (branch and base commit);
  - agent started or resumed (model, session);
  - pushed; PR opened; review comments received; checks failed or passed; ready to complete;
  - PR completed or abandoned.
- **The agent's phases:** a new MCP tool `set_phase(phase, summary)` with the phases `clarify`,
  `plan`, `implement`, `verify`, `fix` and `handoff`. Each phase posts a new message (the spec in the
  agent's own words, options and the chosen plan, verification results, …).
- **Live activity line:** one status message that shows the current tool step ("📖 reading …",
  "🔧 dotnet test"), taken from the stream-json and limited to a few updates a minute.

### Replying mid-task ("what's the progress?")
- **Immediate answer:** any developer message in the thread gets a reply from agentd right away,
  **without waiting for the agent's turn**. It is a status snapshot: the current phase, what the
  agent is doing now (the activity line), elapsed time, the last update, and whether it is waiting
  for the developer.
- **The message still reaches the agent:** at its next tool call (above), so a question or
  instruction is also answered by the agent in context.
- `!status` gives the same snapshot on demand.

### Heartbeat (every minute)
- **Liveness:** while a job is active, agentd refreshes the thread's live status message **every
  minute**, for example "🟢 working · implement · last activity 20 s ago · 12 min elapsed" or
  "⏸ waiting for you since 14:05". The developer can see it is alive without new posts piling up.
- **Stuck agent:** if the agent produces no output for 5 minutes (configurable), agentd posts a
  **⚠️ no activity** message, and posts again when activity resumes. The existing idle timeout still
  stops a hung process.
- **The daemon itself:** if agentd stops or loses the provider, the heartbeat stops updating, and the
  timestamp shows it. `/healthz` reports per-provider status (T2.5).

### Gates
- **Clarify:** open questions go through `ask_developer`, and the job waits.
- **Plan approval, on by default:** the agent posts its plan through `ask_developer` and waits for the
  developer's OK before editing. A work item tagged `ai-auto` skips the wait (the plan is still posted).
- **Hand-off approval** (below).

### Messages always reach the agent
- Every agentd MCP tool call returns any unread developer messages.
- `finish` (and phase changes) are **refused while unread messages exist**, so the agent must address
  them first. This fixes the #5613 gap.

### Verify
The agent may run build, test and lint commands (`dotnet build`/`test`, `npm test`, `helm lint`, …).
This is a per-repository allowlist; still no `git push`, since agentd pushes.

### Review loop
- **States:** after the PR opens, the job goes to **In Review** (a new state) instead of Done.
- **Polling:** agentd polls the PR's threads, votes and policy or build status.
- **Fix rounds:** new reviewer comments or failed checks are posted to the thread, and the **same
  Claude session** resumes to fix them. agentd pushes and replies on each PR thread it addressed.
- **Ready to complete:** when all threads are resolved and the policies pass, agentd posts
  **✅ Ready to complete**. The developer completes the PR; agentd never approves or merges.
- **Limit:** a configurable number of fix rounds per job (then it asks the developer).

### Hand-off (after the PR is completed)
1. **Extract.** The agent resumes once more (`set_phase(handoff)`) and lists the knowledge and
   learnings from the job: facts about the codebase, commands that worked, sharp edges, decisions
   and their reasons, and reviewer feedback worth keeping.
2. **Compare and present.** It compares them with what the repository already records (`AGENTS.md`,
   `CLAUDE.md`, docs, the `.agentd/` kit), then posts **what it would change and where**: additions,
   corrections, removals of stale notes.
3. **Agree.** The developer replies; the agent revises and re-posts **until the developer agrees**
   (any number of rounds, through `ask_developer`). The developer can also decline.
4. **Sync PR.** On agreement the agent makes the changes on a new branch
   (`ai/<id>-knowledge`, from the base branch), and agentd opens a **knowledge sync PR** that is
   linked to the work item and posted in the thread. The same review loop applies to it.
5. **Close-out.** When the sync PR is completed, or when the developer declines to sync, agentd
   **asks the developer to delete the thread**. It deletes (or archives) the thread only on the
   developer's confirmation, and then the job is Done.

### One thread per work item
- A work item has **exactly one** chat thread per provider, for its whole life: retries, reruns,
  review rounds and the hand-off all post there.
- A new job for the same work item reuses the open thread instead of opening another one.
- The thread is removed only at close-out, on the developer's confirmation.

---

## Delivery (each PR under 1,000 lines of code)

1. **Notifications + reliable messaging:**
   - agentd's step messages, `set_phase`, the live activity line and the **every-minute heartbeat**;
   - an **immediate status reply** to any message sent mid-task;
   - unread messages returned on every tool call, and the `finish` guard;
   - **one thread per work item** (reuse the open conversation across jobs).
2. **Clarify, plan approval (on by default, `ai-auto` skips) and verify:** the per-repo command allowlist.
3. **Review loop:** the In Review state; polling PR threads and policies; fix rounds in the same
   session, then push and reply on threads; ready to complete; Done/Cancelled on completed or abandoned.
4. **Hand-off:** extract, compare, present; agree in rounds; the knowledge sync PR; close-out with a
   thread deletion prompt.
5. **Polish:** limits, resolving agentd's own PR threads, tests against the real ADO API, a live
   demo on a sandbox work item.

## Exit criteria (the demo)
On a sandbox work item:
- every step appears in its single thread;
- the plan waits for the developer's OK;
- a review comment on the PR gets fixed and answered;
- "ready to complete" is posted;
- after the developer completes the PR, the hand-off proposes knowledge changes, revises them once
  on feedback, opens a sync PR, and after it is completed asks to delete the thread, then deletes it
  on confirmation.

## Open questions
- **Heartbeat form:** edit one status message every minute (proposed: no clutter), or post a new
  message every minute?
- **The developer's confirmation to delete the thread:** a reply `delete` (or option `1`) in the
  thread. Archive instead of delete when the bot lacks Manage Threads?
- **Fix-round limit** default (proposal: 5).
- **Polling interval** for PRs in review (proposal: 2 minutes).
