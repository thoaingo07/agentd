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
- **Liveness (decided: delete and re-post, with a timestamp):** while a job is active, agentd posts a
  heartbeat **every minute** and **deletes the previous heartbeat**, so the latest status is always
  the last message in the thread and only one heartbeat exists at a time. For example: "🟢 working ·
  implement · last activity 20 s ago · 12 min elapsed · 14:07:00 UTC", or "⏸ waiting for you since
  14:05 · 14:07:00 UTC". This needs `DeleteAsync` on the provider port.
- **Stuck agent:** if the agent produces no output for 5 minutes (configurable), agentd posts a
  **⚠️ no activity** message, and posts again when activity resumes. The existing idle timeout still
  stops a hung process.
- **The daemon itself:** if agentd stops or loses the provider, the heartbeat stops updating, and the
  timestamp shows it. `/healthz` reports per-provider status (T2.5).

### Usage warnings (Claude subscription limits)
- **Source:** Claude Code's stream reports `rate_limit_event` with the utilization of the **5-hour**
  and **weekly** windows and their reset times. agentd already records these as `agent.rate_limit`
  events.
- **Warning:** when either window reaches **80%** (configurable), agentd posts **⚠️ Usage at N% of the
  5-hour (or weekly) window, resets at HH:MM**. It posts once per window per job, again at 95%, and
  the heartbeat shows the latest utilization.
- **At the limit:** the job is paused and resumed after the reset (existing behavior, T1.8). The
  thread says so: "⏸ usage limit reached; resuming at HH:MM".

### Estimates (time and usage)
- **Plan:** the plan includes an **estimate**: expected time (minutes) and expected usage (as a
  share of the 5-hour window, from the size of the change). agentd shows it with the current
  utilization, so the developer can decide before approving, e.g. "est. ~25 min, ~15% of the 5-hour
  window (now at 62%)".
- **At PR time:** agentd reports **actual against the estimate** (elapsed time, usage consumed).
  Both are stored on the job, so later phases (Web UI, learning loop) can calibrate estimates from
  history.

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
   - **usage warnings** at 80% / 95% of the 5-hour and weekly windows;
   - unread messages returned on every tool call, and the `finish` guard;
   - **one thread per work item** (reuse the open conversation across jobs).
2. **Clarify, plan approval (on by default, `ai-auto` skips) and verify:** the plan carries the
   **time and usage estimate**; actual against the estimate at PR time; the per-repo command allowlist.
3. **Review loop:** the In Review state; polling PR threads and policies; fix rounds in the same
   session, then push and reply on threads; ready to complete; Done/Cancelled on completed or abandoned.
4. **Hand-off:** extract, compare, present; agree in rounds; the knowledge sync PR; close-out with a
   thread deletion prompt.
5. **Polish:** limits, resolving agentd's own PR threads, tests against the real ADO API, a live
   demo on a sandbox work item.

### Chat feedback during review (added 2026-10-05)

While the PR is in review, a message in the job's thread starts a **fix round**, like a review comment
(`tngo (in chat): …` becomes the agent's next turn). Before this, it was refused with "This job doesn't
take messages right now". In other states that can't take messages (queued, publishing, failed,
cancelled, done), the reply now says why and what to do instead (`!retry`, `!run <id>`).

### Pause and resume (added 2026-10-05)

- **`!pause`** (or **Pause** on the session page, `POST /api/jobs/{id}/pause`) works on a queued,
  running or waiting job. The agent is stopped, and the job becomes **Paused**. The Claude session,
  worktree, branch and thread are all kept; the agent's exit doesn't cancel it.
- **`!resume`** (or **Resume**) queues it again. The next start reuses the worktree and resumes the
  same session (`--resume`), so the agent continues where it stopped.
- **`!retry` also works on a cancelled job.** Cancelling removes the worktree, but the branch is
  kept, so the retry recreates the worktree at the same path from the branch and resumes the session.
- A paused job still counts as active (one job per work item), takes no messages (the reply points
  to `!resume`), and can be cancelled.

### Progress
- **PR 1a (#26), merged:** unread messages are returned on every tool call, and `finish` is refused
  until they are read; one thread per work item; start, resume and push notifications.
- **PR 1b:**
  - **Phases and activity:** `set_phase` (clarify, plan, implement, verify, fix, handoff) posts a
    phase message. The live activity line comes from Claude's tool calls (`JobActivity`, fed by the
    runner).
  - **Heartbeat:** every minute, **deletes the previous heartbeat and posts a new one with a
    timestamp** (`PostHeartbeats`, `HeartbeatWorker`, provider `DeleteAsync`; the conversation's
    `status_message_id` is the heartbeat).
  - **Status replies:** an **instant status reply** to a message sent mid-task.
  - **Warnings:** usage at **80% / 95%** of the 5-hour and weekly windows, once each; **no activity**
    after 5 minutes, then active again. Once a job ends, its last heartbeat is removed.
  - **Progress:** `report_progress` now posts a visible "⏳" message (the heartbeat owns the status line).
  - Activity, phase and usage are in memory: after a restart they fill again as the agent works.
- **PR 2:**
  - **Gate:** plan approval is on by default (`Agentd:Jobs:RequirePlanApproval`), and the `ai-auto`
    tag skips it. It is enforced, not just requested: while the plan is pending, the agent runs with
    **read-only tools** (`ReadOnlyTools`, plus `--disallowedTools Edit,Write,MultiEdit,NotebookEdit`,
    because `acceptEdits` would otherwise allow edits). `finish` is refused until the plan is approved.
  - **`submit_plan(plan, estimateMinutes, estimateUsagePercent)`:** posts "📝 Plan for your approval"
    with the estimate and the current usage, and the options "✅ Approve plan / ✏️ Request changes".
    The job waits.
    - An approval (`1`, the button label, "approve…", "lgtm", "ok", 👍, …) approves the plan and the
      resume turn may edit.
    - Anything else resumes read-only, with "revise and call `submit_plan` again".
  - **Persistence:** the plan status and estimate are stored on the job (migration 202610040001).
  - **At PR time:** "📊 Actual: N min vs ~M min estimated; usage +X% of the 5-hour window (est. Y%)".
  - **Prompt:** lists the phases (clarify, plan, implement, verify, finish).
  - **Verify:** the allowlist now includes `dotnet build/test/restore/format`, `npm ci/test/run`,
    `helm lint/template` and read-only shell commands.
- **PR 3 (review loop):**
  - **State:** a new **InReview** state (migration 202610050001: `fix_rounds`, `review_state`). With
    `Agentd:Jobs:ReviewLoop` (default on), publishing ends in `OpenForReview` instead of Done, and the
    worktree stays.
  - **Monitor:** `ReviewPullRequests` (`ReviewMonitorWorker`, every `ReviewPollInterval` = 2 min) uses
    ADO's PR status, threads and replies (`IPullRequestService`).
    - **merged** → Done (🎉) and the worktree is removed; **abandoned** → Cancelled.
    - **new open reviewer comments** are posted to the thread ("💬 Review comments") and start a
      **fix round** (InReview → Running with the comments queued) in the same session.
    - Resolved comments are only marked as seen.
    - **every thread resolved** → "✅ Ready to complete", once per round.
  - **After a fix round's push:** a reply on each addressed PR thread.
  - **Limit:** `MaxFixRounds` (5), then it asks the developer to take over.
  - **Own comments:** agentd posts on ADO with the operator's identity, so its PR comments start with
    `🤖 agentd:` and are skipped when reading threads.
  - **Not covered yet:** build/policy failures are not a fix trigger (needs the build logs API).
- **PR 4a (hand-off):**
  - **Trigger:** when a PR in review is merged (`Agentd:Jobs:Handoff`, default on), or on **`!handoff`**
    in the thread for a job that finished before the review loop existed.
  - **Start (`StartHandoff`):** recreates the work item's worktree **at the same path** on
    `ai/<id>-knowledge` from the latest base branch (`IWorktreeManager.RecreateAsync`), so the **same
    Claude session** resumes with full context. The job runs again (`HandoffStatus`: Requested →
    Proposing → Agreed or Declined; migration 202610060001).
  - **Proposal turn:** read-only. The agent lists the knowledge and learnings, compares them with
    `AGENTS.md`/`CLAUDE.md`/docs, and calls **`propose_knowledge`**, which asks with
    "✅ Sync these changes / ✏️ Change something / 🚫 Don't sync".
  - **Answers:**
    - other feedback → revise (still read-only);
    - agreement → writes the changes and `finish` → the **knowledge sync PR** goes through the same
      review loop → merged → "🎓 Knowledge synced", then close-out;
    - decline → "nothing will be synced", then close-out.
  - **Close-out (same PR):** "🧹 All done. Delete this thread? 1. 🗑 Delete thread · 2. 📦 Keep (archive)".
    - `1`/"delete" deletes every thread of the job (provider `DeleteConversationAsync`; Discord
      `DELETE /channels/{thread}`);
    - `2`/"keep" archives it;
    - anything else re-asks. Then the job is Done.
    - agentd handles the answer itself, with no agent turn, and it is not subject to wait reminders or
      the timeout.

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
- **The developer's confirmation to delete the thread:** a reply `delete` (or option `1`) in the
  thread. Archive instead of delete when the bot lacks Manage Threads?
- **Fix-round limit** default (proposal: 5).
- **Polling interval** for PRs in review (proposal: 2 minutes).
