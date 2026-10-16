#!/usr/bin/env bash
set -euo pipefail
if (( $# > 1 )); then
  echo 'Usage: install-desktop.sh [applications-directory]' >&2
  exit 1
fi
FileXfer_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
applications_dir="${1:-${XDG_DATA_HOME:-$HOME/.local/share}/applications}"

# Desktop Entry strings and Exec arguments have separate escaping rules.
desktop_value() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/\\r}"
  value="${value//$'\t'/\\t}"
  value="${value// /\\s}"
  printf '%s' "$value"
}
desktop_exec() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//\$/\\\$}"
  value="${value//\`/\\\`}"
  value="${value//%/%%}"
  desktop_value "\"$value\""
}

mkdir -p -- "$applications_dir"
entry="$(mktemp "$applications_dir/.FileXfer.XXXXXX")"
trap 'rm -f -- "$entry"' EXIT
while IFS= read -r line; do
  case "$line" in
    # Keep field codes out of the executable name; pass the path as data to sh.
    'Exec=@EXEC@') printf 'Exec=/bin/sh -c %s FileXfer %s\n' "$(desktop_exec 'exec "$1"')" "$(desktop_exec "$FileXfer_root/FileXfer")" ;;
    'Path=@PATH@') printf 'Path=%s\n' "$(desktop_value "$FileXfer_root")" ;;
    'Icon=@ICON@') printf 'Icon=%s\n' "$(desktop_value "$FileXfer_root/web/icon.svg")" ;;
    *) printf '%s\n' "$line" ;;
  esac
done < "$FileXfer_root/tools/linux/FileXfer.desktop.in" > "$entry"
chmod 644 "$entry"
mv -f -- "$entry" "$applications_dir/FileXfer.desktop"
printf 'Installed: %s/FileXfer.desktop\n' "$applications_dir"
