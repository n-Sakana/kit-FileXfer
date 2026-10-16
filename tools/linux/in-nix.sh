#!/usr/bin/env bash
set -euo pipefail
FileXfer_args=()
if [[ -n "${FileXfer_ARGUMENTS_BASE64:-}" ]]; then
  while IFS= read -r -d '' argument; do FileXfer_args+=("$argument"); done < <(printf '%s' "$FileXfer_ARGUMENTS_BASE64" | base64 --decode)
fi
"$FileXfer_SOURCE_ROOT/tools/linux/build.sh"
exec "$FileXfer_SOURCE_ROOT/FileXfer.sh" "${FileXfer_args[@]}"
