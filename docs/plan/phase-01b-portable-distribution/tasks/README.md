# Phase 1b — Portable distribution: Tasks

Task index for [Phase 1b — Portable distribution](../README.md). Detail files written 2026-10-05, when the phase
started, from what Phases 1–3 delivered.

**Already in place from Phase 1:** the `agentd` executable with System.CommandLine verbs (`daemon run`, `status`, `run`,
`repo add|list|remove`, `db migrate` in-process, a basic `doctor`), the config home (`AGENTD_HOME`, `agentd.json`,
`AGENTD_*` variables), managed bare clones, and PAT auth for Azure DevOps (`Auth=Pat`).

**Build order** (each PR under 1,000 lines, based on `main`):

| # | ID | Task | Status |
|---|---|---|---|
| 1 | [T1b.2](02-encrypted-secrets.md) | Encrypted secret store (`secrets set/list/remove`, Data Protection, config source, never passed to agents) | ☑ |
| 2 | [T1b.6](06-single-file-publish.md) | Self-contained single-file publish (web assets + migrations inside) for linux-x64/arm64, osx-arm64, win-x64 | ☑ |
| 3 | [T1b.5](05-systemd-service.md) | systemd user service: `daemon install/uninstall/start/stop/status/logs` | ☑ |
| 4 | [T1b.7](07-release-workflow-update.md) | Release workflow (GitHub Releases, checksums, `install.sh`) and `agentd update` with channels | ☑ |
| 5 | [T1b.8](08-docker-image-compose.md) | Docker image (multi-arch, GHCR) + `compose.yaml` with PostgreSQL + "extend with toolchains" guide | ☑ |
| 6 | [T1b.4](04-doctor-full.md) | Full `agentd doctor` incl. `--repo` toolchain and Claude profile checks (and verify headless Claude auth) | ☑ |
| 7 | [T1b.1](01-daemon-cli-socket.md) | Daemon ↔ CLI over a Unix domain socket; `status`/`run` via the daemon when it's up | ☐ |
| 8 | [T1b.10](10-setup-bootstrap.md) | Setup bootstrap: first-run loopback binding, one-time setup token → setup session, `agentd setup-link` | ☐ |
| 9 | [T1b.11](11-setup-service-and-api.md) | `SetupService` use cases + BFF `/api/setup/*` and `/api/settings/*` (Admin, antiforgery, write-only secrets, audit) | ☐ |
| 10 | [T1b.12](12-setup-wizard-settings-ui.md) | `ClientApps/setup` wizard SPA (8 steps with Test buttons) + Settings pages and a Health (doctor) page | ☐ |
| 11 | [T1b.3](03-init-wizard.md) | `agentd init` (headless setup through the same `SetupService`) | ☐ |
| 12 | [T1b.9](09-vps-guide.md) | VPS guide: user, firewall, Tailscale, sizing, backups, upgrades | ☐ |

Secrets come first because `init`, the wizard and Settings all store into it. Publishing, systemd, releases and Docker
come next, so a server can run agentd early, configured with `agentd.json` + `secrets set`. The browser setup follows.

## Decisions on the phase's open questions (proposed 2026-10-05)

- **Azure DevOps on a VPS:** a **PAT per organization** stored with `agentd secrets set AzureDevOps:Pat` (already
  supported by `Auth=Pat`). `az login --use-device-code` stays as the alternative.
- **PostgreSQL on a VPS:** a **Docker container** (the `compose.yaml` service, or one `docker run`), with the
  distribution package documented as the alternative in the VPS guide.
- **Headless Claude Code auth:** a `claude setup-token` token per subscription profile, stored as a secret and given
  only to that profile's agent process. To be **verified against the installed CLI in T1b.4** before the wizard
  depends on it.
