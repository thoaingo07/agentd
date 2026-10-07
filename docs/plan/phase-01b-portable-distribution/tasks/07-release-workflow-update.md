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

## As built (2026-10-06)
- **`.github/workflows/release.yml`** runs on `v*` tags:
  - a build matrix: linux-x64, linux-arm64 and win-x64 on Ubuntu; **osx-arm64 on macOS**, because Apple Silicon needs
    the (ad hoc) signature that only the SDK on macOS adds;
  - each build is `VERSION=${tag#v} build/publish.sh <rid>`, uploaded as `agentd-<rid>` (`agentd-win-x64.exe`);
  - then `sha256sums.txt` over the binaries and `install.sh`, and a GitHub Release (a pre-release when the tag has a
    `-`, e.g. `v0.2.0-beta.1`) with generated notes.
- **`install.sh`:**
  - picks the build from `uname` (Linux x64 or arm64, macOS arm64);
  - takes the latest release, or `AGENTD_VERSION`;
  - **verifies against `sha256sums.txt`** before installing to `~/.local/bin/agentd` (`AGENTD_INSTALL_DIR`);
  - prints the PATH hint and the next steps.
  - It's checked with `shellcheck` in CI.
- **`agentd version`:** "agentd 0.2.0 (linux-x64, commit 3f1c2ab)". A local build is `0.0.0-dev`
  (`Directory.Build.props`), older than any release.
- **`agentd update [--channel stable|beta] [--check] [--to x.y.z]`:**
  - reads GitHub Releases (the repo is public; `GITHUB_TOKEN` is optional, for the rate limit) and picks the newest
    newer release on the channel (`--to` can also go back);
  - downloads this platform's binary and **verifies it against `sha256sums.txt`** (a mismatch changes nothing);
  - renames the running binary to `agentd.previous` and moves the new one in;
  - runs **the new binary's `db migrate`**, then `systemctl --user restart agentd.service` when the service is
    installed. Jobs resume through startup recovery.
  - It refuses on a development build (`agentd.dll` next to it). `AGENTD_UPDATE_REPOSITORY` points it at a fork.
- **Not done yet:** `doctor` warning when a newer release exists; a release has to be tagged to try `install.sh` live.

