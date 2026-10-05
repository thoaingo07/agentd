# T1b.10 — Setup bootstrap: the one-time setup link

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.2 | M | Host, Bff |

## Goal
Before setup completes, the daemon binds loopback only and prints a one-time setup link; the token becomes a
short-lived setup session cookie, and the token dies when setup completes.

## Files
- `src/Agentd.Bff/Setup/SetupToken.cs`: create. 32 random bytes, stored hashed in `~/.agentd/run/setup-token`;
  constant-time compare.
- `src/Agentd.Bff/Setup/SetupSessionAuthentication.cs`: create. `GET /setup?token=…` → HttpOnly SameSite=Strict
  cookie (30 minutes, sliding), redirect to `/setup` without the token.
- `src/Agentd.Host/Cli/Commands/SetupLinkCommand.cs`: create. `agentd setup-link` prints the link (a new token).
- `src/Agentd.Host/HostUrls.cs`: modify. Not set up → force loopback, whatever `Web:Urls` says.

## Implementation
1. "Set up" means `Agentd:Setup:CompletedAt` is set in `agentd.json`.
2. The setup session can only call `/api/setup/*`; everything else needs a normal sign-in.

## Tests
- The token works once per session exchange, fails after completion, and never appears in logs (except the printed link).
- Not set up + `Web:Urls=http://0.0.0.0:7780` → still loopback only.

## Done when
- [ ] First start prints the link; it opens the wizard over an SSH tunnel; after Finish it's dead.
