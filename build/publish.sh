#!/usr/bin/env bash
# Publishes agentd as one self-contained executable per runtime (T1b.6): the web UI, its defaults and the database
# migrations are inside, so a server needs only this file (and PostgreSQL).
#   build/publish.sh [rid ...]          default: linux-x64 linux-arm64 osx-arm64 win-x64
#   VERSION=0.1.0 build/publish.sh      sets the version agentd --version prints
# Output: artifacts/<rid>/agentd (agentd.exe on Windows).
set -euo pipefail
cd "$(dirname "$0")/.."

rids=("$@")
[[ ${#rids[@]} -gt 0 ]] || rids=(linux-x64 linux-arm64 osx-arm64 win-x64)
version_args=()
[[ -n "${VERSION:-}" ]] && version_args=(-p:Version="$VERSION")

# The web first: it's embedded into Agentd.Web.dll at build time.
(cd src/Agentd.Web && npm ci && npm run build)

for rid in "${rids[@]}"; do
  out="artifacts/$rid"
  rm -rf "$out" "artifacts/obj-$rid"
  dotnet publish src/Agentd.Host -c Release -r "$rid" --self-contained \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
    -p:DebugType=embedded "${version_args[@]}" -o "artifacts/obj-$rid"
  mkdir -p "$out"
  # Only the executable ships; the rest of the publish folder (static web asset copies, manifests) isn't needed.
  exe=agentd; [[ "$rid" == win-* ]] && exe=agentd.exe
  mv "artifacts/obj-$rid/$exe" "$out/$exe"
  rm -rf "artifacts/obj-$rid"
  echo "published $out/$exe ($(du -h "$out/$exe" | cut -f1))"
done
