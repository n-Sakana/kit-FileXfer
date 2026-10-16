#!/usr/bin/env bash
set -euo pipefail
FileXfer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
miniui_root="${MINIUI_ROOT:-$FileXfer_root/../miniUI}"
if ! command -v dotnet >/dev/null 2>&1; then
  echo 'FileXfer build needs .NET SDK 9. On NixOS use ./tools/linux/run-nixos.sh.' >&2
  exit 1
fi
if [[ ! -f "$miniui_root/src/MiniUI.Core/Engine/Types.cs" ]]; then
  echo 'Set MINIUI_ROOT to the miniUI source repository (read only).' >&2
  exit 1
fi
dotnet publish "$FileXfer_root/desktop/FileXfer.MiniUI.csproj" -c Release --self-contained false \
  -p:MiniUIRoot="$miniui_root" -o "$FileXfer_root/artifacts/miniui-linux"

if command -v git >/dev/null 2>&1; then
  git -C "$miniui_root" rev-parse HEAD > "$FileXfer_root/artifacts/miniui-linux/MINIUI_SOURCE_REVISION"
fi
