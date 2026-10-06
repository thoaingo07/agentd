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
