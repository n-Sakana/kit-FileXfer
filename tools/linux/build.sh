#!/usr/bin/env bash
set -euo pipefail
FileXfer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
if ! command -v dotnet >/dev/null 2>&1; then
  echo 'FileXfer build needs .NET SDK 9. On NixOS use ./tools/linux/run-nixos.sh.' >&2
  exit 1
fi
dotnet publish "$FileXfer_root/desktop/FileXfer.MiniUI.csproj" -c Release --self-contained false \
  -o "$FileXfer_root/artifacts/miniui-linux"
