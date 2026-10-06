# T1b.7 — Releases, `install.sh` and `agentd update`

| Phase | Depends on | Size | Layer / project |
|---|---|---|---|
| 1b | T1b.6 | M | CI, Host (CLI) |

## Goal
A tagged release publishes the four builds with checksums and an `install.sh`; `agentd update` moves to a newer
release safely.

## Files
- `.github/workflows/release.yml`: create. On `v*` tags: build the web, publish the four RIDs, `sha256sums.txt`,
  a GitHub Release (pre-release for `-beta` tags).
- `install.sh`: create. Detects OS/arch, downloads the binary and the checksums, verifies, installs to
  `~/.local/bin/agentd`.
- `src/Agentd.Host/Cli/Commands/UpdateCommand.cs`: create. `agentd update [--channel stable|beta] [--check]`, `agentd version`.

## Implementation
1. `update`: query GitHub Releases, pick the newest for the channel, download, verify SHA-256, replace the binary
   atomically (rename; keep `agentd.previous`), run `db migrate`, restart the service if installed.
2. In-flight jobs resume after the restart (startup recovery already handles this).
3. No auto-update; `doctor` warns when a newer release exists.

## Tests
- Version selection per channel (fixture release list); checksum mismatch aborts without touching the binary.
- `install.sh` with shellcheck in CI.

## Done when
- [ ] Tagging `v0.1.0` produces a release; `install.sh` installs it; `agentd update` from 0.1.0 → 0.1.1 resumes jobs.
