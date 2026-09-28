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
| `--allowedTools "Read" "Edit" "Bash(git:*)"` | tool allowlist |
| `--disallowedTools ...` | tool denylist |
| `--max-turns <n>` | a hard stop for runaway agents |
| `--append-system-prompt "<text>"` | agentd rules (use MCP tools, commit often, call `finish`) |
| `--mcp-config <file.json>` | registers the agentd MCP server for this job |

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
psi.Environment.Remove("Agentd__Discord__BotToken");

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
