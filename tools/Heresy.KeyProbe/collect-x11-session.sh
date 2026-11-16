#!/usr/bin/env bash
# Read-only X11/XKB snapshot; run separately within EACH display/session.
set -u
if [ "$#" -ne 1 ]; then
  echo "Usage: $0 <short-label e.g. xorg or xephyr>" >&2
  exit 2
fi
label="$(printf %s "$1" | tr -c '[:alnum:]_-' '_')"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
prefix="x11-${label}-${stamp}"
out="${prefix}.txt"
if [ -e "$out" ]; then
  echo "Refusing to overwrite: $out" >&2
  exit 1
fi
{
  printf 'UTC=%s\nlabel=%s\nDISPLAY=%s\nXDG_SESSION_TYPE=%s\nWAYLAND_DISPLAY=%s\n' \
    "$(date -u +%FT%TZ)" "$label" "${DISPLAY-<unset>}" \
    "${XDG_SESSION_TYPE-<unset>}" "${WAYLAND_DISPLAY-<unset>}"
  printf 'XKB_DEFAULT_LAYOUT=%s\nXKB_DEFAULT_VARIANT=%s\nXKB_DEFAULT_OPTIONS=%s\n' \
    "${XKB_DEFAULT_LAYOUT-<unset>}" "${XKB_DEFAULT_VARIANT-<unset>}" "${XKB_DEFAULT_OPTIONS-<unset>}"
  for spec in 'xdpyinfo' 'setxkbmap -query' 'xmodmap -pm' 'xmodmap -pke' 'xset q' 'xinput list --short'; do
    printf '\n========== %s ==========\n' "$spec"
    set -- $spec
    if command -v "$1" >/dev/null 2>&1; then
      "$@" 2>&1 || printf 'Command exited nonzero: %s\n' "$spec"
    else
      printf 'Not installed: %s\n' "$1"
    fi
  done
} >"$out"
if command -v xkbcomp >/dev/null 2>&1 && [ -n "${DISPLAY-}" ]; then
  if xkbcomp -xkb "$DISPLAY" "${prefix}.xkb" 2>>"$out"; then
    printf '\nFull XKB keymap saved to: %s.xkb\n' "$prefix" >>"$out"
  else
    printf '\nFull XKB keymap capture failed (see previous stderr)\n' >>"$out"
  fi
fi
printf 'Saved %s\n' "$out"
