# T2.7 — `ask_developer`, progress and resuming the Claude session

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 2 | T2.4, T2.6, Phase 1 (MCP server, `ClaudeCodeRunner`, scheduler) | M | Agentd.Mcp, Agentd.Application, Agentd.Infrastructure.Claude |

## Goal
Close the conversation loop:
- the agent calls `ask_developer`;
- the question is posted with options;
- the Claude process ends its turn and exits;
- the job waits with no process attached;
- a reply resumes the **same** Claude session with `--resume`.

`report_progress` now updates a status message in chat.

## Files
- `src/Agentd.Mcp/Tools/AskDeveloperTool.cs` — create.
- `src/Agentd.Mcp/Tools/ReportProgressTool.cs` — modify (the Phase 1 version only logged).
- `src/Agentd.Application/Jobs/AskDeveloper.cs`, `ReportProgress.cs` — create.
- `src/Agentd.Application/Jobs/ResumeJobTurn.cs` — create.
- `src/Agentd.Infrastructure.Claude/ClaudeCodeRunner.cs` — modify: turn outcome detection.
- `src/Agentd.Host/Workers/SchedulerWorker.cs` — modify: resume turns, and the wait timeout.
- The core system prompt (`--append-system-prompt`) — modify: the rules for using `ask_developer`.

## Implementation
1. **MCP tool** `ask_developer(question: string, options?: [{id, label}])`:
   - validate: a question of 1–2,000 characters and at most 5 options;
   - call `AskDeveloper`, which enqueues the question message (with options) and records a
     `pending_question` on the job. The job **stays `Running`** until the process exits;
   - return the text: `"Question posted to the developer. End your turn now; you will be resumed with their answer."`
2. **System prompt rule:** "When you need a decision from the developer, call `ask_developer`, then
   stop. Do not continue working or guess."
3. **Runner outcome:** when the process exits with a `result` event and the job has a
   `pending_question`, the outcome is `NeedsHuman`. Then `Job.AskDeveloper(question)` →
   `WaitingForHuman`, and the concurrency slot is released. If the agent kept working after asking
   and finished anyway, the answer is still delivered later as a queued message.
4. **Resume** (`ResumeJobTurn`, triggered by T2.6):
   - the scheduler treats a job with `WaitingForHuman → Running` plus a pending reply as runnable,
     under the same `MaxConcurrent` limit;
   - the prompt is `"Developer <name> answered: <reply>"` (plus the chosen option ID, if any);
   - launch `claude -p <prompt> --resume <sessionId>` with the same flags as the first run.
5. **Queued messages while `Running`:** when a turn ends normally with queued messages, start
   another resume turn with them concatenated, oldest first.
6. **`report_progress(message)`** → `ReportProgress` → `EnqueueAsync(... ReplaceStatusMessage: true)`.
7. **Wait timeout:** `Agents:Claude:WaitForHumanTimeout` (default 3 days). A reminder is posted at
   50% and 90% of it, and the job moves to `Failed` ("no answer") when it expires. The timeout is
   checked by the scheduler every minute.
8. **Restart safety:** `WaitingForHuman` jobs have no process, so nothing needs recovering. A
   reply that arrives while the daemon is down is picked up after restart, from the provider
   backlog (T2.8 and T2.9).

## Tests
- MCP tool validation: too many options, an empty question.
- Runner fixture: a stream-json transcript with a tool_use of `ask_developer` followed by
  `result` → outcome `NeedsHuman`.
- End to end with a fake provider and a stub `claude` script (it echoes its arguments and emits
  canned stream-json): ask → wait → reply → second invocation has `--resume <same id>` and the
  answer in the prompt.
- Timeout: reminders at 50% and 90%, then Failed.

## As built
Both parts landed in one PR: `ask_developer`, live progress and resume turns, plus the wait timeout.

- **`ask_developer(question, options?)`.** `options` is a list of up to 5 labels; the ids `opt1`…
  are assigned when the message is built. The tool moves the job to `WaitingForHuman` **at the call**
  (`AskDeveloperHandler`), rather than recording a pending question and transitioning on exit. The
  `DeveloperQuestionAsked` event then posts the question with buttons through the outbox, in the same
  transaction. The process ends its turn; `HandleAgentExit` ignores a job that isn't Running, and
  the dispatcher frees the slot.
- **Every reply is queued.** `Job.ResumeWith` now always appends `from: reply` to `PendingMessages`,
  including the reply that resumes a waiting job, so the agent always receives it.
- **One resume rule:** a `Running` job with queued replies and no live agent gets a resume turn.
  - `ResumeJobTurn` takes the replies (the version check makes the hand-off exactly-once) and
    returns `claude --resume <same session>` with "The developer replied: - name: text…".
  - `JobDispatcher.TryStartNextAsync` tries resume turns before dequeuing new jobs. It passes the jobs
    it is running, so a busy job never gets a second process.
  - The same rule covers replies to a question and messages queued during a turn.
- **A turn that ends normally with queued replies is not a failure.** `HandleAgentExit` leaves the job
  Running, and the next scheduler pass resumes it.
- **`report_progress`** (`ReportProgressHandler`) records the event and enqueues a progress message
  with `ReplaceStatusMessage`. The dispatcher edits the live status message (T2.5).
- **System prompt:** call `ask_developer` then stop; use `report_progress` for short updates.
- **Wait timeout:** `Agentd:Jobs:WaitForHumanTimeout` (default 3 days), under the existing `Jobs`
  section rather than `Agents:Claude`.
  - New columns `waiting_since` and `wait_reminders` (migration 202610030001; `job_save` gains two
    parameters, and the old signature is dropped).
  - `CheckWaitingJobs` runs from the scheduler every minute. `Job.RemindWaiting(n, expiresAt)` posts
    reminder 1 at 50% and reminder 2 at 90% (each once, via the `WaitReminderSent` event). At 100% the
    job fails with "No answer from the developer within N hours".
  - A reply resets the wait, and a reply that races the check wins through the version check.
- **Tests:** an end-to-end flow through the real dispatcher with a fake runner: ask → process exits →
  waiting with no process → reply → resume turn with `Resume: true`, the same session and worktree,
  and the answer in the prompt. Plus replies during a turn, and MCP tool validation. The `--resume`
  flag itself is covered by the runner's argument tests (Phase 1).

## Done when
- [x] The end-to-end test passes in CI (real dispatcher and use cases, fake runner; the CLI flags are covered by the runner tests).
- [ ] A real manual run against the sandbox ADO repo: a question appears in chat, the reply resumes
  the session, and the transcript shows one continuous session ID.
