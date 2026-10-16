#!/usr/bin/env bash
set -euo pipefail
FileXfer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
FileXfer_host="${FileXfer_MINIUI_DIR:-$FileXfer_root/artifacts/miniui-linux}"
if [[ -f "$FileXfer_host/FileXfer.dll" ]]; then
  if ! command -v dotnet >/dev/null 2>&1; then
    echo 'FileXfer: .NET 9 runtime is missing. On NixOS use: ./tools/linux/run-nixos.sh' >&2
    exit 1
  fi
  exec dotnet "$FileXfer_host/FileXfer.dll" "$@"
fi
echo 'FileXfer MiniUI is not built. Run ./tools/linux/build.sh or ./tools/linux/run-nixos.sh on NixOS.' >&2
exit 1
