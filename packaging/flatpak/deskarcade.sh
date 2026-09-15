#!/bin/sh
# /app/bin/deskarcade: starts Desk Arcade inside the Flatpak sandbox.
set -eu

APP=/app/lib/deskarcade/DeskArcade
ID="${FLATPAK_ID:-com.imperiumgames.DeskArcade}"

# Use the host's ~/.config (exposed by finish-args) instead of ~/.var/app/<id>/config, so settings
# and high scores are shared with the .deb/AppImage and the autostart entry lands where the
# desktop session reads it.
export XDG_CONFIG_HOME="$HOME/.config"

# The single-instance lock and the --signal socket live in TMPDIR. /tmp is private to each sandbox
# instance, but $XDG_RUNTIME_DIR/app/<id> is shared by all of them, so "flatpak run ... --signal next"
# reaches the running game.
export TMPDIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/app/$ID"
mkdir -p "$TMPDIR"

for arg in "$@"; do
  case "$arg" in
    --signal | --SIGNAL) exec "$APP" "$@" ;;
  esac
done

# "Start when I sign in" writes Exec="<path of the running binary>", which is /app/... and does not
# exist on the host. Point such entries at "flatpak run" instead: now, and every few seconds while
# the game runs (the check exits with the sandbox).
AUTOSTART="$XDG_CONFIG_HOME/autostart/deskarcade.desktop"
fix_autostart() {
  if [ -f "$AUTOSTART" ] && grep -q '^Exec="*/app/' "$AUTOSTART"; then
    sed -i -e "s|^Exec=.*|Exec=flatpak run $ID|" -e "s|^Icon=.*|Icon=$ID|" "$AUTOSTART" || true
  fi
}
fix_autostart
(
  while kill -0 "$$" 2>/dev/null; do
    sleep 5
    fix_autostart
  done
) &

# exec keeps this process id (2 in the sandbox), which the tray's D-Bus name in finish-args relies on.
exec "$APP" "$@"
