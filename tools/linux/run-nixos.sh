#!/usr/bin/env bash
set -euo pipefail
FileXfer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
# All command arguments travel as positional parameters, not shell source.
exec nix-shell "$FileXfer_root/tools/linux/shell.nix" --run \
  'exec bash "$FileXfer_SOURCE_ROOT/tools/linux/in-nix.sh"' \
  --argstr FileXferRoot "$FileXfer_root" --argstr arguments "$(if (( $# )); then printf '%s\0' "$@"; fi | base64 | tr -d '\n')"
