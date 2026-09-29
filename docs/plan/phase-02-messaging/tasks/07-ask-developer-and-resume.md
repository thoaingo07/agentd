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

## Done when
- [ ] The stub-based end-to-end test passes in CI.
- [ ] A real manual run against the sandbox ADO repo: a question appears in chat, the reply resumes
  the session, and the transcript shows one continuous session ID.
