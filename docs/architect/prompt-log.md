# agentd: the prompt log

> Decided 2026-10-12: keep every prompt agentd sends to an agent, so the team can see what the agents were asked and
> learn from it (which prompts work, which rules confuse them, what a model or effort change does).

## What's kept

One row per agent turn (`agentd.agent_prompts`), written as the turn starts and finished when it ends:

| Field | |
|---|---|
| `source`, `source_id` | what it was for: `job` (the job id), `idea`, `chat`, `review` (a chat `!review`), `review-session`, `review-ask` (the ask thread), `review-fix`, `follow-up` |
| `step` | a job's cycle step (`implement`, `review`, …) |
| `session`, `resume` | the agent's session, and whether the turn continued it |
| `model`, `effort`, `profile` | what was asked for (none: the runner's default) |
| `rules_hash` | the system rules appended to the agent's own (`--append-system-prompt`), stored once per distinct text in `agentd.agent_prompt_rules` (SHA-256) |
| `prompt` | what was sent (`-p`) |
| `outcome`, `error` | `ok`, `error`, `usage-limit`, `timeout` or `cancelled`, and why it failed |
| `reply` | a thread turn's final answer; a job's whole transcript is already on its page |
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

## Next

The Prompts page: a list with filters (source, step, model, outcome, date), a prompt with its rules and reply, and
links from a job's, idea's or review's page (`GET /api/prompts`, `GET /api/prompts/{id}`).
