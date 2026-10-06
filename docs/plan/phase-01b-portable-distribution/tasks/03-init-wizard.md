# T1b.3 — `agentd init` (headless setup)

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.2, T1b.11 | M | Host (CLI) + Application (SetupService) |

## Goal
A guided terminal setup for servers without a browser: the same steps as the web wizard (§5a), through the same
`SetupService` use cases, writing `agentd.json` and secrets.

## Files
- `src/Agentd.Host/Cli/Commands/InitCommand.cs`: create. Prompts per step; `--non-interactive` with flags/env for automation.
- Uses `SetupService` (T1b.11) for database, Azure DevOps, SSH key, models, chat, repositories and the doctor review.

## Implementation
1. Steps: database (connection string → secret, then `db migrate`), Azure DevOps (organization URL, PAT → secret, or
   `az`), git SSH key (generate `~/.agentd/ssh/id_ed25519`, print the public key and where to add it), Claude profile
   (paste the `claude setup-token` token → secret, or "logged in on the server"), chat (optional), repositories,
   web binding (`127.0.0.1:7780` by default), then the doctor report.
2. Re-running `init` keeps existing values unless changed (shows "set · updated …" for secrets).

## Tests
- Each step through a fake console (prompts and answers) writes the expected `agentd.json` keys and secrets.
- `--non-interactive` with missing required values fails with the list of what's missing.

## Done when
- [ ] A fresh `AGENTD_HOME` + `agentd init` → `agentd doctor` all ✅ (manual, on the VPS demo).
