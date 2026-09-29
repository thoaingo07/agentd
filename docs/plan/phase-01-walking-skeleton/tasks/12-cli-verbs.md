# T1.12 — `agentd` CLI skeleton and the `~/.agentd` config home

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1 | T1.2, T1.4, T1.11 | M | `Agentd.Host` |

## Goal
Turn the Host into the **`agentd` executable**: a daemon plus CLI verbs, with configuration in a
**config home**, so the walking skeleton already runs the way it will on a VPS
([deployment.md §1–2](../../../architect/deployment.md)). Phase 1b adds install, `init`, secrets,
releases and Docker on top of this.

## Files
- `src/Agentd.Host/Cli/AgentdCli.cs`: create. The System.CommandLine root and verbs.
- `src/Agentd.Host/Cli/Commands/{Daemon,Status,Run,Repo,Db,Doctor}Command.cs`: create.
- `src/Agentd.Host/ConfigHome.cs`: create. Resolves `AGENTD_HOME` / `~/.agentd` and creates the folders.
- `src/Agentd.Host/Program.cs`: modify. `return await AgentdCli.InvokeAsync(args);`.
- `Directory.Packages.props`: modify. `System.CommandLine`.
- `tests/Agentd.Web.Tests` / new `tests/Agentd.Host.Tests`: create CLI tests.

## Implementation
1. **Verbs in Phase 1:**
   - `agentd daemon run` (also the default with no args): starts the web host and workers.
   - `agentd status [--all]`: a job table (ID, work item, repo, state, elapsed, attempt, PR / last error).
   - `agentd run <workItemId> [--repo <name>]`: `ClaimWorkItem(force: true)`. The repo match and the
     one-active-job rule still apply.
   - `agentd repo add <url> [--name] [--base] [--tag] [--area-path …]` / `repo list` / `repo remove <name>` (T1.4).
   - `agentd db migrate`: runs `SchemaMigrator` in-process. The Host references `Agentd.Migrator`, so
     update the architecture rule to "only the AppHost and the Host reference the Migrator".
   - `agentd doctor` (basic): checks PostgreSQL, git, `az`/PAT for each organization, SSH reachability
     of each registered repo, and the `claude` CLI presence and version.
2. **Config home:**
   - `AGENTD_HOME` or `~/.agentd`, with `config/`, `repos/`, `worktrees/`, `logs/`, `claude/` and
     `run/` created on first use;
   - `config/agentd.json` is added as a configuration source after appsettings, and the `AGENTD_`
     environment prefix is added on top;
   - `WorktreeRoot` and the clone root default to paths inside the home.
3. **Transport:** in Phase 1 the CLI verbs use PostgreSQL through the Application layer (the daemon
   picks the jobs up). The Unix socket to the daemon comes in Phase 1b.
4. **Exit codes:** 0 ok · 2 usage · 3 not found / no repo match · 4 an active job already exists ·
   5 a doctor check failed.
5. Aspire runs `daemon run`. The README documents `dotnet run --project src/Agentd.Host -- status`
   etc.

## Tests
- Parsing and help for every verb; an unknown verb → exit 2.
- `run` twice for the same work item → exit 4 the second time.
- `status` formatting (a snapshot test).
- `ConfigHome` honors `AGENTD_HOME`, and creates the folders with restrictive permissions.

## Done when
- [ ] `agentd --help` lists the verbs; `agentd daemon run` behaves like today's Host.
- [ ] `agentd repo add git@erm-azdo:v3/ermsystem/Portal/sysmin` registers `sysmin` with base branch `develop`.
- [ ] `agentd doctor` reports each check with a fix hint.
