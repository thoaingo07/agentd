# Phase 1b — Portable distribution: Tasks

Task index for [Phase 1b — Portable distribution](../README.md).

> **Detail files are written when this phase starts**, based on what Phase 1 delivered.

| ID | Task | Status |
|---|---|---|
| T1b.1 | Daemon ↔ CLI over a Unix domain socket; `status`/`run` via the daemon when it's up | ☐ detail pending |
| T1b.2 | Encrypted secret store (`secrets set/list/remove`, Data Protection, config source, never passed to agents) | ☐ detail pending |
| T1b.3 | `agentd init` wizard (config, SSH key generation, secrets, Claude profile auth, DB connection, web binding) | ☐ detail pending |
| T1b.4 | Full `agentd doctor` incl. `--repo` toolchain and Claude profile checks | ☐ detail pending |
| T1b.5 | systemd user service: `daemon install/uninstall/start/stop/status/logs` | ☐ detail pending |
| T1b.6 | Self-contained single-file publish (web assets + migrator inside) for linux-x64/arm64, osx-arm64, win-x64 | ☐ detail pending |
| T1b.7 | Release workflow (GitHub Releases, checksums, `install.sh`) and `agentd update` with channels | ☐ detail pending |
| T1b.8 | Docker image (multi-arch, GHCR) + `compose.yaml` with PostgreSQL + "extend with toolchains" guide | ☐ detail pending |
| T1b.10 | Setup bootstrap: first-run loopback binding, one-time setup token → setup session, `agentd setup-link` | ☐ detail pending |
| T1b.11 | `SetupService` use cases + BFF `/api/setup/*` and `/api/settings/*` (Admin, antiforgery, write-only secrets, audit) | ☐ detail pending |
| T1b.12 | `ClientApps/setup` wizard SPA (8 steps with Test buttons) + Settings pages and a Health (doctor) page in the dashboard | ☐ detail pending |
| T1b.9 | VPS guide: user, firewall, Tailscale, sizing, backups, upgrades | ☐ detail pending |
