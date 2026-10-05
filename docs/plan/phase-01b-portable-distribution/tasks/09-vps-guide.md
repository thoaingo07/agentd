# T1b.9 — VPS guide

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.5, T1b.7, T1b.8 | S | docs |

## Goal
A step-by-step guide for a fresh Ubuntu VPS: an unprivileged `agentd` user, firewall, Tailscale or an SSH tunnel,
sizing, PostgreSQL, backups and updates.

## Files
- `docs/guides/vps.md`: create. Linked from the README and deployment.md §7–8.

## Implementation
1. Covers both install paths (binary + systemd, Docker Compose), Cloudflare Tunnel + Access (§7.1), and restoring
   from a backup.

## Tests
- Followed end to end on a fresh VPS for the Phase 1b demo.

## Done when
- [ ] The demo in the phase README works by following only this guide.
