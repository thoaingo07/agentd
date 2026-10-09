# Working with jobs

A **job** is one run of the agent on one work item. Each job has its own Discord thread, its own git worktree and
branch, and its own Claude session, so jobs never interfere with each other.

## Start a job

| How | Where |
|---|---|
| Add the tag **`ai-workflow`** to a work item (plus `repo:<name>` when several repositories are registered) | Azure DevOps; agentd polls every minute |
| `!run 1234` | the Discord channel |
| **Run work item** | the web dashboard |
| `agentd run 1234` | the server's command line |

agentd claims the work item by adding **`ai-in-progress`**, so it's never picked up twice. At most
`Scheduler:MaxConcurrent` jobs run at once (2 by default); the others wait as **Queued**.

## The cycle

1. **Clarify.** The agent reads the work item and the code. If something is unclear it asks in the thread and waits.
   Answer with the option's number or in your own words. A question nobody answers is reminded, then times out after
   3 days.
2. **Plan.** It posts a plan with an estimate of time and usage. Reply **`1`** (or `approve`) to go ahead, or say
   what to change. Work items tagged **`ai-auto`** skip the approval.

   The plan is written so **another model can implement it** without guessing (for example DeepSeek, when the
   implement step runs there). It has these parts:
   - **Goal:** "done when", tied to the acceptance criteria.
   - **Approach:** the chosen approach, and the options rejected.
   - **Changes:** the exact files and symbols to touch.
   - **Steps:** small, ordered, each naming the existing code to copy.
   - **Guardrails:** what must not change, and the edge cases and error handling to get right.
   - **Verify:** the exact commands, and the tests to add.
   - **Risks and questions.**

   The implementer follows it step by step. If a step can't work as written, it asks you instead of improvising.
   **Each step says what it runs on** when it starts, in the thread and on the session page:
   ```text
   📝 Plan · Claude · claude-opus-5-5 · effort high
   🔨 Implement · deepseek · deepseek-flash
   🔧 Fix round 1 · deepseek · deepseek-flash
   ```
   The model is the one Claude Code reports at start-up. A resumed step (after your answer, or a restart) isn't
   repeated. `!review` and `!chat` say theirs in the opening message (`🧠 claude-opus-5-5 · effort high`).
3. **Implement and verify.** It edits, builds and tests in its worktree. A heartbeat in the thread shows what it's
   doing, for example `🔧 dotnet test (running for 2 min) · CPU 180% · RAM 2.1 GB`.
4. **Pull request.** It commits; agentd pushes the branch `ai/<id>-<title>` and opens the PR, linked to the work
   item. Agents can never push themselves.
5. **Review loop.** New comments on the PR, or messages in the thread, start a **fix round** in the same session
   (up to 5). The agent replies in the PR thread it fixed. When every comment is resolved, agentd says the PR is ready
   to complete.
6. **Merge.** You complete the PR in Azure DevOps. agentd notices within 2 minutes.
7. **Hand-off.** The agent proposes knowledge and learnings to keep in the repository (for example notes in
   `AGENTS.md`). Agree, ask for changes, or decline.
8. **Close-out.** "Delete this thread? `1` delete · `2` keep (archived)". The full conversation stays in agentd either way.

After the merge the job is finished. **Messages in its thread still get answers** (the same session, read-only),
but nothing is changed or published anymore; for more changes start a new run with `!run <id>`. agentd never opens
a second PR for a job whose PR was merged.

## States

| State | Meaning |
|---|---|
| Queued | waiting for a free slot (or for a usage limit to reset) |
| Preparing | creating the worktree and the thread |
| Running | the agent is working |
| WaitingForHuman | it asked you something, or waits for plan approval |
| Publishing | pushing and opening the pull request |
| InReview | the PR is open; comments start fix rounds |
| Paused | stopped by you (`!pause`); everything is kept for `!resume` |
| Done | the PR is merged (or the job finished without one) |
| Failed | something went wrong; see the last error, then `!retry` |
| Cancelled | stopped for good (`!cancel`, the Cancel button, or the PR was abandoned) |

## Steering a running job

All of these work in the job's thread. See [Chat commands](chat.md) for the full list.

- **Ask "what's the progress?"** anytime. You get the status at once, including the latest output of a running
  command.
- **Any message goes to the agent.** If it's busy, it reads the message at its next step, and `finish` is refused
  until it has.
- **`!pause` / `!resume`:** stop now and continue later in the same session.
- **`!cancel`** ends the job. **`!retry`** runs a failed or cancelled job again, resuming where it was.

## Usage limits

On a Claude subscription, a job that hits the 5-hour or weekly limit waits until the limit resets, then resumes the
same session by itself. The thread warns at 80% and 95% of each window.

## Where things live

| What | Where |
|---|---|
| The worktree | `~/.agentd/worktrees/<repo>/wi-<id>` (kept 3 days after a failure, so `!retry` can resume; removed after a merge) |
| The transcript | `~/.agentd/logs/` (and in the web UI) |
| The branch | `ai/<id>-<slug>`; never deleted by agentd |
