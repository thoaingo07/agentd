# T1.7 — `ClaudeCodeRunner`: launch, stream, timeouts, secret-free environment

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2, T1.6, T1.8 | L | `Agentd.Infrastructure.Claude` |

## Goal
Run one `claude` process per job turn inside the job's worktree, stream its stdout into the event
log, enforce timeouts and cancellation, and make sure **no daemon secret ever reaches the agent**.

## Files
- `src/Agentd.Infrastructure.Claude/ClaudeOptions.cs` — create: `Binary`, `Model`, `PermissionMode`, `AllowedTools`, `MaxTurns`, `IdleTimeout`, `TranscriptRoot`.
- `src/Agentd.Infrastructure.Claude/ClaudeArgsBuilder.cs` — create.
- `src/Agentd.Infrastructure.Claude/SafeEnvironment.cs` — create.
- `src/Agentd.Infrastructure.Claude/ClaudeCodeRunner.cs` — create: `IAgentRunner`.
- `src/Agentd.Infrastructure.Claude/McpConfigWriter.cs` — create: per-job `mcp.json`.
- `tests/Agentd.Infrastructure.Tests/Claude/*` — create (with a fake `claude` script).

## Implementation
1. **Arguments** (`ClaudeArgsBuilder`, one `ArgumentList` entry each; verify the flags against the
   pinned Claude Code version):
   ```
   -p <prompt>
   --session-id <uuid>        (first run)   |   --resume <uuid>   (later runs)
   --output-format stream-json --verbose
   --model <Model>
   --permission-mode <PermissionMode>
   --allowedTools <tool> <tool> …
   --max-turns <MaxTurns>
   --mcp-config <TranscriptRoot>/wi-<id>/mcp.json
   --append-system-prompt <agentd rules>
   ```
2. **The MCP config file** points at `http://127.0.0.1:<port>/mcp` with an `Authorization: Bearer
   <per-job token>` header (token from `IMcpTokenIssuer`, T1.9). It is written with `0600` permissions
   and deleted when the job ends.
3. **The environment** (`SafeEnvironment.Build`) starts from `Environment.GetEnvironmentVariables()`, then:
   - **removes** every key starting with `Agentd__`, plus `ConnectionStrings__*`, `AZURE_*`,
     `ASPNETCORE_*`, `DOTNET_*` and `OTEL_*`;
   - keeps `PATH`, `HOME`, `LANG` and git/ssh variables;
   - adds only what Claude needs for the single Phase 1 profile, which is a **Claude subscription**
     (the user has no API key):
     - **dev machine:** nothing extra, so it uses the logged-in `~/.claude` subscription
       (`claude auth status --json` → `authMethod: claude.ai`);
     - **optional:** `ConfigDir` (sets `CLAUDE_CONFIG_DIR` to a dedicated login) or an `OAuthToken`
       secret from `claude setup-token`, injected as the documented environment variable;
     - **never** an `ANTHROPIC_API_KEY` unless a profile explicitly has one.
4. **Process:**
   - `WorkingDirectory` = the worktree, `UseShellExecute = false`, stdout and stderr redirected;
   - start it and register it in an in-memory map `jobId → Process` (for `IsRunning` and `Cancel`).
5. **Streaming:**
   - read stdout with `ReadLineAsync` and append each raw line to `<TranscriptRoot>/wi-<id>/transcript.jsonl`;
   - parse each line (T1.8) and call `RecordAgentOutput`;
   - stderr goes to the daemon log at Warning level.
6. **Idle timeout:** a watchdog restarted on every line. If `IdleTimeout` passes with no output →
   kill the process tree (`Process.Kill(entireProcessTree: true)`) → the outcome is `TimedOut`.
7. **Cancellation:** `Cancel(jobId)` → kill the process tree → the outcome is `Cancelled`.
8. **Exit:** await the exit, then return `Exited(code)` together with the last `result` event summary
   (turns, error subtype).
9. **Subscription usage limits:** if the `result` event (or stderr) reports a usage or rate limit,
   return `UsageLimited(resetAt?)` instead of a failure. The use case then **re-queues** the job
   (`not_before` = the reset time, or a backoff) and keeps its session, so it resumes later with
   `--resume` ([model-profiles.md §4a](../../../architect/model-profiles.md#4a-subscription-usage-limits)).
   Default `MaxConcurrent: 1` for the subscription profile.

## Tests
- `Agentd.Infrastructure.Tests` with a **fake `claude`** (a small script that prints fixture
  stream-json lines, sleeps, or exits with a code):
  - the arguments are passed exactly;
  - the resume path uses `--resume`;
  - lines are streamed and recorded in order;
  - the idle timeout kills the process;
  - cancel kills it;
  - **env test:** set `Agentd__AzureDevOps__Pat=secret` and `ConnectionStrings__agentd=x` in the
    daemon, and the fake script dumps its environment → neither is present.
- The transcript file exists and matches the stdout lines.

## As built
- `--allowedTools` is passed as **one comma-separated value**, so the variadic flag can't swallow
  other arguments. stdin is redirected and closed immediately.
- The environment is rebuilt from scratch: `Agentd__*`, `AGENTD_*`, `ConnectionStrings__*`,
  `AZURE_*`, `OTEL_*`, `DOTNET_*`, `ASPNETCORE_*`, `services__*`, `ANTHROPIC_API_KEY`,
  `ANTHROPIC_AUTH_TOKEN` and `AZURE_DEVOPS_EXT_PAT` are removed. Subscription auth is only added via
  `ConfigDir` (`CLAUDE_CONFIG_DIR`) or the optional `OAuthToken`.
- The per-job `mcp.json` (with a bearer token) is written with mode 0600 and deleted after the run; the
  token is revoked.
- Parsed events go straight to `IEventStore` (the Application `RecordAgentOutput` use case stays for
  other callers).
- Verified with an opt-in live test against the real CLI (`AGENTD_LIVE_CLAUDE=1`).

## Done when
- [ ] A real `claude` run in a scratch worktree streams events into the `events` table.
- [ ] The secret-stripping test is green (this is an exit criterion of the phase).
- [ ] No shell strings; the prompt is passed as a single argument.
