# T1.8 — stream-json parser

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2 | S | `Agentd.Infrastructure.Claude` |

## Goal
Turn each line of `claude --output-format stream-json` output into a typed, provider-neutral
`AgentEvent`, which is stored in the event log and later drives the Web UI (Phase 3) and cost
tracking (Phase 6).

## Files
- `src/Agentd.Application/Jobs/AgentEvent.cs` — create: a neutral event record (`Type`, `Payload`).
- `src/Agentd.Infrastructure.Claude/StreamJsonParser.cs` — create.
- `tests/Agentd.Infrastructure.Tests/Claude/Fixtures/*.jsonl` — create: recorded real runs.
- `tests/Agentd.Infrastructure.Tests/Claude/StreamJsonParserTests.cs` — create.

## Implementation
1. **Parse** with `System.Text.Json` (`JsonDocument`); never throw on unknown input.
2. **Mapping** (verify the event and field names against the pinned Claude Code version, and record
   the fixtures from a real run):

   | stream-json | `AgentEvent.Type` | payload kept |
   |---|---|---|
   | `system` / `init` | `process.started` | `session_id`, model, tools |
   | `assistant` → text block | `assistant.text` | text |
   | `assistant` → `tool_use` block | `tool.call` | tool name, id, input (truncated to 8 KB) |
   | `user` → `tool_result` block | `tool.result` | tool id, `is_error`, content (truncated to 16 KB) |
   | `result` | `turn.result` | `subtype`, `num_turns`, `is_error`, usage (input/output/cache tokens), `total_cost_usd` |
   | anything else | `agent.unknown` | the raw line (truncated) |
   | invalid JSON | `agent.parse_error` | the raw line (truncated) |

3. **One line can yield several events**, because an assistant message can hold multiple blocks.
4. **Redaction before storage:** mask obvious secrets in the payloads (patterns for
   `Authorization: Bearer …`, PAT-like base64 strings, `sk-…` keys). This is a first pass; a fuller
   redaction step comes in Phase 3.
5. **The `result` event** also sets the outcome summary used by `HandleAgentExit` (for example
   `error_max_turns` → a failure reason).

## Tests
- Fixture tests:
  - a normal run (text + Bash tool + Edit tool + result);
  - a tool error;
  - max turns reached;
  - a malformed line;
  - an unknown event type;
  - a large tool output is truncated;
  - a secret in a tool output is masked.
- Order is preserved, and every input line yields at least one event.

## As built
- Fixtures are **real transcripts from Claude Code 2.1.284** (a text-only run and a Read-tool run),
  with the `system/init` details of the local setup stripped.
- The CLI emits a **`rate_limit_event`** (`status`, `resetsAt`, and `unifiedWindows` utilization for
  `five_hour` / `seven_day`). `RunTracker` treats a non-`allowed` status (or a 429 result) as
  **usage-limited**, with the reset time, so `HandleAgentExit` defers the job.
- The CLI waits 3 s for stdin unless stdin is closed, so the runner must redirect stdin (T1.7).

## Done when
- [ ] The fixtures come from a real `claude` run (committed and scrubbed).
- [ ] The parser never throws on any input (a fuzz test over random lines).
