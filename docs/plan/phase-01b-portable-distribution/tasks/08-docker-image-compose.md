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
