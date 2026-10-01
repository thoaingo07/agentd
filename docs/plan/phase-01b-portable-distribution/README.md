# Phase 1b — Portable distribution (standalone VPS)

**Goal:** install and run agentd on **any Linux VPS** and let it work on **any repository**, without
a source checkout: `curl … | bash` → `agentd daemon install` → open the **one-time setup link** →
complete the **web setup wizard** (the headless path uses `agentd init`).
The equivalent Docker Compose setup also works. This follows the shape of
[NetClaw](https://github.com/netclaw-dev/netclaw).

Design: [deployment.md](../../architect/deployment.md) · Builds on **Phase 1** (CLI skeleton, config
home, managed clones) and **Phase 3** (BFF, antiforgery, Web UI foundations), which the setup
wizard needs. **It runs after Phase 3.**

---

## Scope

**In**

- **Daemon ↔ CLI over a Unix socket** (`~/.agentd/run/agentd.sock`). `status` and `run` go through
  the daemon when it's up.
- **Web setup wizard + Settings** ([deployment.md §5a](../../architect/deployment.md#5a-setup-ui-first-run-wizard-and-settings)):
  - a one-time setup link (loopback + token), and a separate `ClientApps/setup` SPA at `/setup`;
  - steps for admin access, database, Azure DevOps, Git SSH key, models, chat, repositories, and a
    review with the doctor report;
  - **Settings** pages (Admin) with write-only secret fields and an audit trail;
  - `SetupService` use cases shared with the CLI.
- **`agentd init`:** the headless alternative to the wizard. A guided setup that writes `agentd.json`, generates agentd's SSH key (and prints
  the public key to add to Azure DevOps/GitHub), stores secrets, sets up Claude authentication per
  profile, configures the database connection, and chooses the web binding.
- **Encrypted secrets:** `agentd secrets set|list|remove`, with `secrets.json` protected by Data
  Protection (keys in `~/.agentd/keys`), loaded as a configuration source and never given to agents.
- **Full `agentd doctor`**, including `--repo` toolchain checks and Claude profile authentication.
- **systemd user service:** `agentd daemon install|uninstall|start|stop|status|logs`.
- **Releases:**
  - a GitHub Actions release workflow producing self-contained single-file builds (linux-x64,
    linux-arm64, osx-arm64, win-x64) with the web assets included;
  - checksums and an `install.sh`;
  - `agentd update [--channel stable|beta]`.
- **Docker:** a multi-arch image on GHCR (agentd + git + openssh + Node 24 + Claude Code CLI,
  optional `az`), a `compose.yaml` with PostgreSQL, and docs for extending the image with repo
  toolchains.
- **VPS guide:** an unprivileged `agentd` user, firewall, Tailscale, sizing, backups and updates.

**Out:** per-repo dev containers (v2), multi-host scheduling.

## Tasks

Task index: [tasks/README.md](tasks/README.md) (the detail files are written when this phase starts).

## Exit criteria (the demo)

- On a **fresh Ubuntu VPS**:
  1. `install.sh` → `agentd daemon install && agentd daemon start` → open the setup link over an
     SSH tunnel → the wizard configures the database, Azure DevOps (PAT), the git SSH key, a Claude
     profile and the `sysmin` repo;
  2. `agentd doctor` is all ✅;
  3. tagging a work item produces a PR in `sysmin`, with nothing but the installed binary, its
     config home and PostgreSQL on the machine.
- The same flow works with `docker compose up` using the published image.
- `agentd update` moves to a newer release and resumes in-flight jobs.
- No secret appears in plain text on disk (except in `~/.agentd/keys`) or in any agent's environment.

## Risks / open questions for review

- **Headless Claude Code authentication** (a long-lived token vs an API key): verify it against the
  installed CLI version.
- **Azure DevOps on a VPS:** a PAT per organization (recommended) or `az login --use-device-code`?
- **PostgreSQL on the VPS:** a Docker container (proposed) or the distribution package?
