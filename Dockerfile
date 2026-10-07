# syntax=docker/dockerfile:1
# agentd + git + ssh + Node 24 + the Claude Code CLI (T1b.8): `docker compose up -d` (compose.yaml).
# Target repos' toolchains (dotnet, java, …) aren't included: extend this image (docs/architect/deployment.md §7B).

# The build runs on the builder's own platform and cross-publishes for the target (no emulation for the .NET build).
FROM --platform=$BUILDPLATFORM node:24-trixie-slim AS node
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm \
 && ln -s /usr/local/lib/node_modules/npm/bin/npx-cli.js /usr/local/bin/npx
WORKDIR /src
COPY . .
ARG TARGETARCH
ARG VERSION=0.0.0-dev
RUN rid="linux-$([ "$TARGETARCH" = arm64 ] && echo arm64 || echo x64)" \
 && VERSION="$VERSION" build/publish.sh "$rid" \
 && cp "artifacts/$rid/agentd" /agentd

# Debian 13 (trixie): git 2.47 (agentd wants ≥ 2.40).
FROM node:24-trixie-slim
ARG CLAUDE_CODE_VERSION=latest
ARG INSTALL_AZ=false
# libssl: .NET's https; tini: a proper PID 1 (signals, zombie processes from agents' tools).
RUN apt-get update \
 && apt-get install -y --no-install-recommends ca-certificates curl git openssh-client libssl3t64 tini \
 && if [ "$INSTALL_AZ" = true ]; then curl -fsSL https://aka.ms/InstallAzureCLIDeb | bash; fi \
 && rm -rf /var/lib/apt/lists/* \
 && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
 && npm cache clean --force \
 && useradd --create-home --uid 10001 --shell /bin/bash agentd \
 && install -d -m 0700 -o agentd -g agentd /home/agentd/.agentd
COPY --from=build /agentd /usr/local/bin/agentd
USER agentd
WORKDIR /home/agentd
# Config, secrets, keys, clones, worktrees and logs: keep this volume. No secret is baked into the image.
# The folder exists (agentd's, 0700) before the VOLUME, so a new volume gets that owner instead of root.
ENV AGENTD_HOME=/home/agentd/.agentd
VOLUME /home/agentd/.agentd
ENTRYPOINT ["tini", "--", "agentd"]
CMD ["daemon", "run"]
