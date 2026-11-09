#!/usr/bin/env bash
# Smoke-test Kit mode (--standard: MewUI's own X11 platform host + the MewVG OpenGL backend) on a Linux box with no
# display: starts an Xvfb screen, runs the built demo on it, clicks through every tab, opens and closes each of the
# four DevTools with their shortcuts, and fails when the process dies or exits early. Screenshots of each step land
# in MEWSTAGE_JOURNEY_OUT (default: no screenshots) when ImageMagick's `import` is installed.
#
# Needs: Xvfb, xdotool (tab clicks and shortcuts; without it only the launch is checked), Mesa's software GL
# (libgl1-mesa-dri; LIBGL_ALWAYS_SOFTWARE is set so a GPU-less runner uses llvmpipe), and a Debug build of
# src/MewStage.Demo so the DevTools are in the build.
#
# Usage:  scripts/kit-smoke-x11.sh [path-to-dotnet] [path-to-MewStage.Demo.dll]
#   MEWSTAGE_JOURNEY_OUT   directory for the screenshots (created)
#   KIT_SMOKE_DISPLAY      the X display number to start Xvfb on (default 77)
set -euo pipefail
DOTNET="${1:-dotnet}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DLL="${2:-$ROOT/src/MewStage.Demo/bin/Debug/net10.0/MewStage.Demo.dll}"
DISP=":${KIT_SMOKE_DISPLAY:-77}"
OUT="${MEWSTAGE_JOURNEY_OUT:-}"
[ -e "$DLL" ] || { echo "[kit-smoke] FATAL: $DLL not built (dotnet build src/MewStage.Demo -c Debug)" >&2; exit 1; }
command -v Xvfb >/dev/null || { echo "[kit-smoke] FATAL: Xvfb not installed" >&2; exit 1; }
[ -n "$OUT" ] && mkdir -p "$OUT"

Xvfb "$DISP" -screen 0 1600x1000x24 >/dev/null 2>&1 &
XVFB=$!
cleanup() { kill "${APP:-}" 2>/dev/null || true; sleep 0.5; kill "$XVFB" 2>/dev/null || true; }
trap cleanup EXIT
sleep 1
export DISPLAY="$DISP" LIBGL_ALWAYS_SOFTWARE=1

LOG="${OUT:-/tmp}/kit-smoke.log"
"$DOTNET" "$DLL" --standard >"$LOG" 2>&1 &
APP=$!
sleep 7
kill -0 "$APP" 2>/dev/null || { echo "[kit-smoke] FATAL: the demo exited within 7 s:" >&2; cat "$LOG" >&2; exit 1; }
echo "[kit-smoke] Kit mode is up on $DISP (X11 platform host + MewVG GL)"

shot() { if [ -n "$OUT" ] && command -v import >/dev/null; then import -display "$DISP" -window root "$OUT/$1.png"; fi; }
alive() { kill -0 "$APP" 2>/dev/null || { echo "[kit-smoke] FATAL: the demo died at '$1':" >&2; cat "$LOG" >&2; exit 1; }; }
shot kit-buttons

if command -v xdotool >/dev/null; then
  MAIN=$(xdotool search --name "MewStage Kit" | head -n 1)
  eval "$(xdotool getwindowgeometry --shell "$MAIN")"   # X, Y, WIDTH, HEIGHT of the main window (centered, no WM)
  # The tab headers along the window's top edge.
  i=0
  for dx in 100 160 208 258 323 386; do
    i=$((i + 1))
    xdotool mousemove $((X + dx)) $((Y + 12)) click 1; sleep 1.5
    shot "kit-tab$i"; alive "tab $i"
  done
  xdotool mousemove $((X + 30)) $((Y + 12)) click 1; sleep 1
  xdotool mousemove $((X + 600)) $((Y + 500)) click 1; sleep 0.5

  # Each DevTool on, a screenshot, off again. The two that open OS windows (visual tree, profiler) need the platform
  # host; the shortcuts would throw on the embedded host, which is why the demo unmaps them there. Xvfb runs no window
  # manager, so keyboard focus follows the pointer: the pointer is parked on the main window's bottom-left corner,
  # clear of the tool windows, before every shortcut. The tool windows are counted by title to prove they opened and
  # closed.
  windows() { xdotool search --name "$1" 2>/dev/null | wc -l; }
  PARK_X=$((X + 40)); PARK_Y=$((Y + HEIGHT - 20))        # the status bar's left end: under no tool window
  for entry in "ctrl+shift+i inspector -" "ctrl+shift+p performance -" "ctrl+shift+t visual-tree Live Visual Tree" "ctrl+shift+alt+p profiler MewUI Profiler"; do
    read -r key name title <<<"$entry"
    xdotool mousemove "$PARK_X" "$PARK_Y"; sleep 0.3
    xdotool key --delay 150 "$key"; sleep 2.5
    xdotool mousemove $((X + 200)) $((Y + 200)); sleep 0.5; xdotool mousemove $((X + 400)) $((Y + 300)); sleep 0.5
    shot "kit-devtools-$name"; alive "$name on"
    if [ "$title" != "-" ]; then
      [ "$(windows "$title")" -ge 1 ] || { echo "[kit-smoke] FATAL: no '$title' window after $key" >&2; exit 1; }
    fi
    xdotool mousemove "$PARK_X" "$PARK_Y"; sleep 0.3
    xdotool key --delay 150 "$key"; sleep 1.5
    alive "$name off"
    if [ "$title" != "-" ]; then
      [ "$(windows "$title")" -eq 0 ] || { echo "[kit-smoke] FATAL: '$title' window still open after the second $key" >&2; exit 1; }
    fi
    echo "[kit-smoke] DevTools $name: toggled on and off, process alive"
  done
else
  echo "[kit-smoke] xdotool not installed: launch checked only"
fi

alive "end"
echo "[kit-smoke] OK"
