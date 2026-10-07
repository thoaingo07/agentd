# T1b.4 — Full `agentd doctor`

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.2 | M | Host (CLI) + Application |

## Goal
Extend `doctor` to every check in deployment.md §6, with a fix per failure, and a `--repo` toolchain check.

## Files
- `src/Agentd.Application/Setup/DoctorChecks.cs`: create. The checks as Application use cases (also used by the wizard's
  review step and the Health page, T1b.12).
- `src/Agentd.Host/Cli/Commands/DoctorCommand.cs`: modify. Print ✅/⚠️/❌ with the fix; exit code 1 on any ❌.

## Implementation
1. New checks: config valid; secrets decrypt; schema up to date (pending migrations count); `git` ≥ 2.40; SSH key
   present and `git ls-remote` per repository; Node ≥ 24; `claude` CLI version; **each model profile authenticates**
   (`claude auth status`, then a tiny prompt with `--max-turns 1`); chat providers connect; free disk in `~/.agentd`.
2. `--repo <name>`: the commands in the repo's kit `verify` section resolve on `PATH` (until the kit exists: `dotnet`,
   `npm`, `node` per detected project files).
3. **Verify headless Claude auth** against the installed CLI (the open question): a `claude setup-token` token in
   the profile's environment vs a login in `CLAUDE_CONFIG_DIR`; record the result in deployment.md §5.

## Tests
- Each check with a fake process runner / fake store: ok, warning and failure paths with their fixes.
- Exit codes: 0 all ok, 1 any failure (warnings don't fail).

## Done when
- [ ] `agentd doctor` covers deployment.md §6; `--repo` reports missing toolchains.
- [ ] The headless Claude auth decision is recorded.

## As built (2026-10-06)
- **Levels:** ✅ ok, ⚠️ warning (passes, shown with its fix), ❌ failure (exit 5). A check that crashes becomes one ❌
  line instead of stopping the run.
- **Checks, in order:**
  1. **Configuration:** the startup validators (`IStartupValidator`).
  2. **Secrets:** how many, and whether all decrypt.
  3. **PostgreSQL:** connected, schema present, and **pending migrations** (`SchemaMigrator.PendingAsync`, known
     `[Migration]` versions minus `schema_version`).
  4. **git ≥ 2.40:** a warning when older.
  5. **SSH key:** agentd's own; a warning with the `ssh-keygen` recipe when there's none.
  6. **Azure DevOps.**
  7. **Repositories:** `ls-remote` per repository.
  8. **Node ≥ 24:** a warning.
  9. **Claude Code CLI**, plus **a live test prompt** in the agents' environment (`--skip-live` skips it).
  10. **Chat:** each enabled provider's health.
  11. **Worktrees:** disk use.
  12. **Version:** a warning when a newer release exists (installed releases only).
- **`--repo <name>`:** `IWorktreeManager.ListFilesAsync` (the base branch in the managed clone) → `DoctorChecks.Toolchains`.
  - The files map to tools: `*.sln(x)`/`*.csproj`/`global.json` → dotnet; `package.json` → node plus npm, pnpm or
    yarn by lockfile; `pom.xml` → mvn and java; gradle → java; `go.mod` → go; `Cargo.toml` → cargo; Python files →
    python3; `Chart.yaml` → helm; Dockerfile or compose → docker; `Taskfile` → task; `Makefile` → make.
  - Each tool is checked with its version command and names the (shallowest) file that needs it.
  - This stands in until the kit's `verify` section exists.
- **Live run on the development machine (`--repo sysmin`):**
  - all ✅: configuration, Postgres (17 migrations), git 2.54, Azure DevOps, the sysmin repo, Claude (a test prompt in
    2.1 s), Discord, worktrees;
  - ⚠️ for no agentd SSH key, and Node v22 (< 24);
  - sysmin needs dotnet 10.0.301, node, npm, helm and task, all present.
- **Headless Claude auth:** the logged-in path is verified. The `claude setup-token` path is checked by the same live
  prompt and is to be confirmed on the VPS demo (deployment.md §5).
- **Later:** the checks move into an Application `DoctorChecks` use case when the setup wizard's Review step (T1b.11)
  needs them in the BFF.

