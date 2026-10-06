# Claude Code (headless) reference

agentd runs one `claude` process per job, in that job's worktree.

## Key flags

| Flag | Use in agentd |
|---|---|
| `-p, --print "<prompt>"` | non-interactive run of one turn; the process exits when the turn ends |
| `--session-id <uuid>` | the daemon picks the session ID on the first run |
| `--resume <uuid>` | continue that session with a new prompt (developer reply, restart) |
| `--output-format stream-json --verbose` | stream events as JSON lines for parsing and logging |
| `--model <id>` | e.g. `claude-opus-5-5` |
| `--permission-mode <mode>` | e.g. `acceptEdits`; avoid `bypassPermissions` outside a sandbox |
| `--allowedTools "Read" "Edit" "Bash(git:*)"` | tool allowlist. Headless runs can't answer a prompt, so anything not listed is refused and the refusal goes back to the agent. agentd's defaults are in `ClaudeOptions.AllowedTools` / `ReadOnlyTools`: read and search tools; `git status/diff/add/commit/log/show/fetch` (no push: agentd pushes); dotnet/npm build and test. `Agentd:Claude:AllowedTools` entries are **added** to the defaults. For a compound command (`a && b`, `a \| b`), every part must match. |
| `--disallowedTools ...` | tool denylist |
| `--permission-prompt-tool mcp__agentd__permission` | a tool call outside the allowlist goes to agentd's MCP tool instead of being refused. A person decides in the job's chat thread (later also the Web UI). Contract, checked against CLI 2.1.289: arguments `tool_name`, `input`, `tool_use_id`; reply JSON text `{"behavior":"allow","updatedInput":<input>}` or `{"behavior":"deny","message":…}`. The CLI waits for the answer; agentd sets `MCP_TOOL_TIMEOUT` (15 min) above its own permission timeout (10 min, then deny). |
| `--max-turns <n>` | a hard stop for runaway agents |
| `--append-system-prompt "<text>"` | agentd rules (use MCP tools, commit often, call `finish`) |
| `--mcp-config <file.json>` | registers the agentd MCP server for this job |
| `--strict-mcp-config` | only the servers from `--mcp-config`. Without it, a subscription login also loads the operator's claude.ai connectors (Drive, Docs, …) into the agent's session (checked on CLI 2.1.289: `['probe', 'claude.ai Claude Docs', 'claude.ai Google Drive']` → `['probe']`). |

## Environment per model profile

See [model-profiles.md](../model-profiles.md). The runner sets these per process:

| Env var | Use |
|---|---|
| `ANTHROPIC_BASE_URL` | an Anthropic-compatible endpoint (DeepSeek, GLM, a gateway for Gemini, …); unset for Anthropic |
| `ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_API_KEY` | the profile's credential |
| `ANTHROPIC_MODEL` | the main model at that provider (plus `--model`) |
| `ANTHROPIC_DEFAULT_HAIKU_MODEL` | the cheap model used for background tasks |
| `CLAUDE_CONFIG_DIR` | an isolated config, credentials and session store, one per subscription account |
| `API_TIMEOUT_MS` | request timeout for slower providers |

Sessions live under `CLAUDE_CONFIG_DIR`, so `--resume` only works with the same profile.

## Per-job MCP config example

```json
{
  "mcpServers": {
    "agentd": {
      "type": "http",
      "url": "http://127.0.0.1:7777/mcp",
      "headers": { "Authorization": "Bearer <per-job-token>" }
    }
  }
}
```

The per-job token lets the daemon know which job made a tool call, such as `ask_developer`.

## Parsing stream-json

- Each stdout line is one JSON event: `system` (init, which includes the `session_id`),
  `assistant` (text and tool_use blocks), `user` (tool results), and `result`, which comes last and
  carries `subtype`, `num_turns`, `total_cost_usd` and `is_error`.
- A `result` event with an error subtype (for example `error_max_turns`) → the job moves to `Failed`,
  unless it can be retried.

## Running it from .NET

```csharp
var psi = new ProcessStartInfo(opts.Binary)
{
    WorkingDirectory = job.WorktreePath,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
};
foreach (var arg in BuildArgs(job, prompt)) psi.ArgumentList.Add(arg);   // never build a shell string
psi.Environment.Remove("Agentd__AzureDevOps__Pat");                      // strip daemon secrets
psi.Environment.Remove("Agentd__Messaging__Providers__Discord__BotToken");
psi.Environment.Remove("Agentd__Messaging__Providers__Telegram__BotToken");

using var proc = Process.Start(psi)!;
while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
    await events.PublishAsync(StreamJsonParser.Parse(job.Id, line), ct);
await proc.WaitForExitAsync(ct);
```

Use `ArgumentList` rather than a single argument string, because prompts contain untrusted text.

## Alternative: Claude Agent SDK

The Agent SDK (TypeScript/Python) offers the same features (sessions, resume, MCP, permissions) as a
library API. From .NET, driving the CLI is the practical choice.

- Docs: https://docs.claude.com/en/docs/claude-code/sdk
- CLI reference: https://docs.claude.com/en/docs/claude-code/cli-reference

## Permission requests (agentd)

When the agent needs a command outside its allowlist, the CLI calls `mcp__agentd__permission`, and agentd:

1. **Hard denies**, which no one can approve: `git push` (agentd pushes), `sudo`, piping a download into a
   shell, `rm -rf /` or `~`, and anything touching `~/.agentd` (agentd's own files and secrets).
2. **Allows** without asking when every part of the command is allowlisted or remembered.
   - **Splitting:** a shell command is split into the simple commands it runs, the way a shell reads it
     (`ShellCommand`, fixed 2026-10-06). Separators (`;`, `&&`, `||`, `|`, `&`, newlines, parentheses) count only
     outside quotes. Commands inside `$( … )` and backticks are checked too, even inside double quotes. Here-document
     bodies are data. Loop and `if` syntax (`for … in`, `do`, `then`, `done`, `fi`) isn't a command.
   - **Keys:** each part's key is its command, e.g. `Bash(sed:*)`. Only tools whose second word picks an action (git,
     npm, dotnet, docker, kubectl, helm, az, gh, make, …) add it: `Bash(npm install:*)`.
   - **Coverage:** a broader grant covers the narrower keys, so `Bash(npm:*)` covers `npm install`.
   - **Default allowlist:** besides git/read tools, it now has common text tools (`echo`, `printf`, `sed`, `sort`,
     `uniq`, `cut`, `tr`, `diff`, `jq`, `pwd`, `basename`, `dirname`, `stat`).
   - Before the fix, quoted patterns and loops became keys like `Bash(probe\:*)` or `Bash(do for:*)`. Those never
     matched again, so the same approval was asked over and over. Migration `202610130001` removes them.
   - **Auto mode** (`Agentd:Jobs:PermissionMode = Auto`): everything that isn't hard-denied is allowed without asking,
     and still recorded as a request decided by `auto` (Web UI timeline). The agent can then run any command as
     agentd's Unix user, so use a dedicated unprivileged user (deployment.md §7A). The CLI's own
     `--permission-mode auto` was tested (2.1.290) and escalates loops, `rm`, `npm --version`, `curl` and `git init`,
     so it doesn't reduce the questions much. `bypassPermissions` would skip agentd's hard denies.
3. **Otherwise asks** in the job's thread: **1** allow once · **2** allow for this job · **3** always allow
   in this repository · **4** deny. People answer with the number, a word (`allow`, `always`, `deny`, …),
   or `!approve [job|always]` / `!deny`. The **Web UI** asks too: a banner on the session page with the
   same four buttons (`POST /api/jobs/{id}/permissions/{requestId}` with `choice` = `once`, `job`, `repo`
   or `deny`; 409 `already_decided` when someone answered first), and a 🔐 badge on the dashboard's job
   row. The first answer wins (an atomic routine), and the decision is announced in the thread.
4. "This job" and "always" are stored as rules (`permission_rules`). The Settings page lists them
   (`GET /api/permissions/rules`), and **Admins revoke** one (`DELETE /api/permissions/rules/{id}`), so
   the agent is asked again. Every request, decision and revoke is an event (`permission.requested`,
   `permission.decided`, `permission.revoked`).
5. No answer within `Agentd:Jobs:PermissionTimeout` (10 min) is a deny. The agent is told why and carries on.

## What a busy agent is doing (agentd, 2026-10-06)

- **Running vs finished:** a tool call marks the job's activity as *running* until its result arrives. The status, the
  heartbeat and `!status` say "🔧 dotnet test (running for 6 min)" instead of "(6 min ago)".
- **Permission decisions update it:** allowed (by a person or in auto mode) → "🔧 <command>", running. Denied or expired
  → "⛔ denied …". It never stays on "⏳ waiting for permission" after the answer.
- **Live output:** Claude Code 2.1.290 emits `system/task_started` (`task_id`, `tool_use_id`, `session_id`) for a
  command it tracks. It writes the output as it goes to `<tmp>/claude-<uid>/<encoded cwd>/<session>/tasks/<task_id>.output`.
  - agentd follows that file (`CommandOutput.Tail`: the last 15 lines, at most 4 KB, read with shared access) for the
    instant status reply, `!status` and the stuck warning.
  - This is undocumented CLI behavior: if the file isn't there, the line is shown without output.
- **Stuck warning:** while one command runs more than `Jobs:StuckAfter` (5 min), the thread gets "⚠️ The agent has been
  running <command> for N min. It reads your messages when this ends; `!pause` stops it." plus the tail. Otherwise it's
  "No activity" as before.
- **Command time limit:** `Claude:CommandTimeout` (10 min, the CLI's own default made explicit and configurable) is passed
  as `BASH_MAX_TIMEOUT_MS`, so a hung command fails with a timeout.

## Resource use per job (agentd, 2026-10-06)

- **What's measured:** every 5 s, `ResourceSamplerWorker` reads Linux `/proc`, with no external tools:
  - **each running job:** CPU (percent of one core, from utime+stime deltas, USER_HZ = 100) and resident memory, summed
    over the agent's **whole process tree** (claude plus everything it started: `dotnet test`, `npm`, `helm`…);
  - **the job's worktree size:** every minute;
  - **the machine:** CPU busy (`/proc/stat`), memory (`MemTotal`, `MemAvailable`), and the disk holding the worktrees.
- **Where it shows:** the heartbeat and status lines add "CPU 180% · RAM 2.1 GB · disk 450 MB" (only while the sample is
  under a minute old). `!status` adds a "Machine:" line.
- **Warnings:** each running job's thread gets "⚠️ **Low disk**" (under 5 GB or 5% free) and "⚠️ **Low memory**" (under
  10% available) once, when they start to apply.
- **Limits:**
  - Work run **inside Docker** happens in the Docker daemon, so it shows in the machine numbers, not the job's.
  - Off Linux, nothing is sampled.
  - The Web UI shows these in a follow-up PR.

