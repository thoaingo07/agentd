# T1b.8 — Docker image and `compose.yaml`

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.6 | M | build, docs |

## Goal
A multi-arch image on GHCR with agentd, git, openssh, Node 24 and the Claude Code CLI, and a compose file with
PostgreSQL, so `docker compose up` gives the same setup flow.

## Files
- `Dockerfile`: create. Runtime `debian:stable-slim` + git + openssh-client + Node 24 + `@anthropic-ai/claude-code`;
  the linux single-file binary; a non-root `agentd` user; `AGENTD_HOME=/home/agentd/.agentd` (a volume).
- `compose.yaml`: create. agentd + `postgres:17`, volumes for both, the web on `127.0.0.1:7780` only.
- `.github/workflows/release.yml`: modify. Build and push `linux/amd64,linux/arm64` with buildx.
- `docs/architect/deployment.md` §7B: the "extend with toolchains" example and how secrets come in (env or `secrets set`
  inside the container).

## Implementation
1. The image never contains secrets; the setup link is printed in the container log.
2. Optional `az` via a build arg.

## Tests
- CI builds the image and runs `agentd --version` and `agentd doctor` (expected failures listed) inside it.

## Done when
- [ ] `docker compose up` → setup link in the logs → wizard → a job runs (manual).

## As built (2026-10-06)
- **`Dockerfile`:**
  - the build stage runs on the builder's platform and cross-publishes for the target with `build/publish.sh`, so the
    .NET build doesn't need emulation;
  - the runtime is `node:24-trixie-slim` (Debian 13: git 2.47) plus `libssl3t64`, git, openssh, curl, `tini` and
    `@anthropic-ai/claude-code` (`CLAUDE_CODE_VERSION`), with `az` optional (`INSTALL_AZ`);
  - user `agentd` (10001); `~/.agentd` is created 0700 and owned by agentd *before* the `VOLUME`, so new volumes aren't
    root's;
  - `ENTRYPOINT tini -- agentd`, `CMD daemon run`.
- **`compose.yaml`:**
  - postgres 17, a one-shot `migrate`, and `agentd`, all with `network_mode: host` on 127.0.0.1;
  - `POSTGRES_PASSWORD` is required in `.env`; `POSTGRES_PORT` and `AGENTD_VERSION` are optional;
  - `stop_grace_period: 60s`.
  - Why host networking: see deployment.md §7B.
- **Release:** `image` job builds amd64 and arm64 with buildx and QEMU, and pushes `:<version>`, `:latest` (release) or
  `:beta` (pre-release) to GHCR, with the GHA cache.
- **CI:** a `docker` job builds amd64 and checks the tools, the uid, and that `doctor` reports without crashing.
- Verified locally with `docker compose up` (see deployment.md §7B).

