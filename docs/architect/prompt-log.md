# agentd: the prompt log

> Decided 2026-10-12: keep every prompt agentd sends to an agent, so the team can see what the agents were asked and
> learn from it (which prompts work, which rules confuse them, what a model or effort change does).

## What's kept

One row per agent turn (`agentd.agent_prompts`), written as the turn starts and finished when it ends:

| Field | |
|---|---|
| `source`, `source_id` | what it was for: `job` (the job id), `idea`, `chat`, `review` (a chat `!review`), `review-session`, `review-ask` (the ask thread), `review-fix`, `follow-up`, `incident` (an incident's investigation, [incident-monitor.md](incident-monitor.md)) |
| `step` | a job's cycle step (`implement`, `review`, …) |
| `session`, `resume` | the agent's session, and whether the turn continued it |
| `model`, `effort`, `profile` | what was asked for (none: the runner's default) |
| `rules_hash` | the system rules appended to the agent's own (`--append-system-prompt`), stored once per distinct text in `agentd.agent_prompt_rules` (SHA-256) |
| `prompt` | what was sent (`-p`) |
| `outcome`, `error` | `ok`, `error`, `usage-limit`, `timeout` or `cancelled`, and why it failed |
| `reply` | a thread turn's final answer |
| transcript | every event of the turn's run (text, tool calls and results, like a job's Transcript): a thread turn's in `agentd.agent_prompt_events`, written once as it ends (at most `Agentd:PromptLog:MaxEvents`, 5000); a job turn's are its job's `agent.*` events while it ran (added 2026-10-14) |
| `started_at`, `finished_at` | |

Prompts and replies longer than `Agentd:PromptLog:MaxChars` (200,000) are cut, saying so.

## How

- Both runners record through `PromptRecorder` (Infrastructure.Claude): `ClaudeCodeRunner` (jobs) and
  `ClaudeBrainstormAgent` (every thread turn). Another runner records the same way.
- Routines: `agent_prompt_start` (stores the rules once; parallel callers with the same rules share one row),
  `agent_prompt_finish` (once), `agent_prompt_delete_before`.
- **Never fails a turn:** a log that can't be written is logged as a warning and skipped.
- **Retention:** `Agentd:PromptLog:RetentionDays` (90; 0 keeps them forever), deleted by the hourly sweep with the rules
  nobody uses any more. A rules row's `last_used` is touched (under its row lock) by every prompt that uses it, so a
  prune never removes rules a new prompt is using. `Agentd:PromptLog:Enabled = false` turns it off.
- **What's in it:** prompts carry work item and PR text and the repository's names, never agentd's secrets (agents never
  get them). It's as sensitive as a job's transcript, and shown to the same people.

## Reading it

**Admins only** (decided 2026-10-13): prompts carry work item, PR and code text from everyone's work, like the System
log.

- `GET /api/prompts`: newest first, 50 a page (at most 100, `limit`). Ask again with `before` = the last id while
  `hasMore`. Filters: `source`, `sourceId` (a job, idea, chat, review, review session or ask thread), `step`, `model`,
  `outcome` (`running` = not finished yet) and `from` (a time). Each row has the settings, how it ended, the first 300
  characters (`preview`), and how long the prompt and reply were. Routine `agent_prompt_list`.
- `GET /api/prompts/{id}`: one prompt, whole, with its system rules and the reply. Routine `agent_prompt_get`.
- `GET /api/prompts/{id}/transcript`: the turn's events in the job Transcript's shape (`EventVm`: `seq`, `jobId`, `ts`,
  `type`, `payload`), at most 5000. Routine `agent_prompt_transcript`; `agent_prompt_events_add` writes a thread turn's.
- `GET /api/prompts/conversations?source=&before=&limit=`: one row per conversation (a job, idea, chat, review, …):
  its turns, first and last time, the latest turn's outcome, whether one still runs, the models used, and its first
  prompt's start; the latest activity first, 50 a page (at most 100), `before` = the last `lastId`. Routine
  `agent_prompt_conversations`.
- `GET /api/prompts/conversations/{source}/{sourceId}`: its turns, oldest first, whole (at most 500). Routine
  `agent_prompt_conversation`.

**Settings → Prompts** in the web UI (added 2026-10-14, by conversation):

- `/settings/prompts` lists conversations grouped by kind (Jobs, Ideas, Chats, Chat reviews, Reviews, Review asks,
  Review fixes, Follow-ups), filtered by kind; **All prompts** (`/settings/prompts/all`) is the flat list, its filters
  in the address (`?source=job&sourceId=42`).
- `/settings/prompts/{source}/{sourceId}` shows one conversation like a work item's Transcript: every turn, oldest
  first, with its model, effort, outcome and time, the prompt and its system rules (folded), the transcript drawn with
  the Transcript's own rows (text, tool calls with their results, questions; read as each turn comes into view), and
  the reply. The job, idea and review pages' **Prompts** link opens their conversation; `/settings/prompts/{id}` still
  shows one prompt. Sources with a page link to it (job, idea, chat, review session).
