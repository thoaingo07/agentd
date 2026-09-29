# T1.12 — CLI verbs: `agentd run` and `agentd status`

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2, T1.11 | S | `Agentd.Host` |

## Goal
Give operators a way to start a specific work item immediately (without waiting for a poll), and to
see what agentd is doing, before the chat and the Web UI exist.

## Files
- `src/Agentd.Host/Cli/CliRouter.cs` — create: dispatches `args` before the web host starts.
- `src/Agentd.Host/Cli/RunCommand.cs`, `StatusCommand.cs` — create.
- `src/Agentd.Host/Program.cs` — modify: `if (CliRouter.TryHandle(args, out var exitCode)) return exitCode;`.
- `tests/Agentd.Host.Tests/Cli/*` or `Agentd.Application.Tests` — create.

## Implementation
1. **Verbs** (plain `args` parsing to keep dependencies small; switch to `System.CommandLine` only if
   the verbs grow):
   - `agentd run <workItemId> [--repo <name>]` → `ClaimWorkItem(id, force: true)`. It skips the
     tag and state filters, but the repo match and the "one active job per work item" rule still
     apply. It prints the job ID and exits 0.
   - `agentd status [--all]` → `GetJobStatus`. It prints a table of the active jobs (or all jobs
     from the last 24 hours with `--all`): ID, work item, repo, state, elapsed time, attempt, PR URL
     or last error.
   - no verb → start the daemon (the default).
2. **Transport:** the CLI talks **directly to PostgreSQL** through the Application layer, using the
   same configuration. The job is picked up by the running daemon's scheduler. This works whether or
   not the daemon is running (it just stays `Queued` until the daemon starts).
3. **Exit codes:** 0 = ok, 2 = usage error, 3 = not found or no repository match, 4 = an active job
   already exists for the work item.
4. The Aspire AppHost doesn't use the CLI. Document how to use it in `README.md`
   (`dotnet run --project src/Agentd.Host -- run 1234`).

## Tests
- `run` on a new work item creates a Queued job; `run` twice → exit 4 on the second call; an
  unknown verb → usage + exit 2.
- `status` output formatting (a snapshot test).

## Done when
- [ ] `agentd run <id>` starts the demo work item without waiting for a poll.
- [ ] `agentd status` shows the two concurrent jobs during the exit-criteria demo.
