#!/bin/sh
# Installs agentd from GitHub Releases (T1b.7):
#   curl -fsSL https://github.com/thoaingo07/agentd/releases/latest/download/install.sh | sh
# Options (environment): AGENTD_VERSION=0.2.0 (default: the latest release), AGENTD_INSTALL_DIR (default: ~/.local/bin).
# The binary is checked against the release's sha256sums.txt before it's installed.
set -eu

REPO="${AGENTD_REPO:-thoaingo07/agentd}"
VERSION="${AGENTD_VERSION:-latest}"
DIR="${AGENTD_INSTALL_DIR:-$HOME/.local/bin}"

case "$(uname -s)-$(uname -m)" in
  Linux-x86_64) rid=linux-x64 ;;
  Linux-aarch64 | Linux-arm64) rid=linux-arm64 ;;
  Darwin-arm64) rid=osx-arm64 ;;
  *)
    echo "agentd: no build for $(uname -s) $(uname -m). Builds: linux-x64, linux-arm64, osx-arm64; on Windows download agentd-win-x64.exe." >&2
    exit 1
    ;;
esac

if [ "$VERSION" = latest ]; then
  base="https://github.com/$REPO/releases/latest/download"
else
  base="https://github.com/$REPO/releases/download/v${VERSION#v}"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT
echo "Downloading agentd ($rid, $VERSION)…"
curl -fsSL "$base/agentd-$rid" -o "$tmp/agentd-$rid"
curl -fsSL "$base/sha256sums.txt" -o "$tmp/sha256sums.txt"

expected="$(grep " agentd-$rid\$" "$tmp/sha256sums.txt" | cut -d ' ' -f 1)"
if command -v sha256sum > /dev/null 2>&1; then
  actual="$(sha256sum "$tmp/agentd-$rid" | cut -d ' ' -f 1)"
else
  actual="$(shasum -a 256 "$tmp/agentd-$rid" | cut -d ' ' -f 1)"
fi
if [ -z "$expected" ] || [ "$expected" != "$actual" ]; then
  echo "agentd: the download doesn't match sha256sums.txt; nothing was installed." >&2
  exit 1
fi

mkdir -p "$DIR"
chmod +x "$tmp/agentd-$rid"
mv "$tmp/agentd-$rid" "$DIR/agentd"
echo "Installed $("$DIR/agentd" version) to $DIR/agentd"
case ":$PATH:" in
  *":$DIR:"*) ;;
  *) echo "Add $DIR to your PATH, e.g.: echo 'export PATH=\"$DIR:\$PATH\"' >> ~/.profile" ;;
esac
echo "Next: agentd daemon install && agentd daemon start"
