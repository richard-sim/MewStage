#!/usr/bin/env bash
# Run the MEWSTAGE_VK_SMOKE-gated Vulkan tests on Linux with Mesa lavapipe (a software Vulkan implementation).
#
# Without root: downloads the libvulkan1 + mesa-vulkan-drivers .deb packages, extracts them into a prefix, points the
# Vulkan loader at the lavapipe ICD, sets MEWSTAGE_VK_SMOKE=1 and runs the test projects. With lavapipe already
# installed system-wide (apt install mesa-vulkan-drivers) the provisioning is skipped and the system ICD is used.
#
# Usage:  scripts/ci-vulkan-render-test.sh [path-to-dotnet] [extra dotnet test args...]
#   VK_PREFIX   where to extract the packages (default /tmp/vk/prefix)
#   TEST_PROJECTS  space-separated test project paths (default: every tests/*.Tests project with Vulkan tests)
#
# Two traps this script avoids on purpose:
#   * the ICD manifest is found by GLOB (lvp_icd*.json): mesa <= 24.x named it lvp_icd.x86_64.json, mesa >= 25.x
#     names it lvp_icd.json. A hard-coded old name finds no driver, every gated test SKIPS, and the run is green.
#   * VK_ICD_FILENAMES and VK_DRIVER_FILES are exported on SEPARATE lines: `export A="$B" B=...` on one line expands
#     $B before B is assigned, which silently yields an empty driver list, with the same green-and-empty result.
# The test output must say how many Vulkan tests RAN; a green run with zero is a failure of the setup, not a pass.
set -euo pipefail
DOTNET="${1:-dotnet}"
shift || true
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PFX="${VK_PREFIX:-/tmp/vk/prefix}"
TEST_PROJECTS="${TEST_PROJECTS:-tests/MewStage.Rendering.Tests tests/MewStage.Ui.Tests tests/MewStage.Host.Tests tests/MewStage.Demo.Tests}"

ICD=""
# System-wide lavapipe first.
for cand in /usr/share/vulkan/icd.d/lvp_icd*.json; do
  [ -e "$cand" ] && ICD="$cand" && break
done

if [ -z "$ICD" ]; then
  if [ ! -e "$PFX/usr/lib/x86_64-linux-gnu/libvulkan.so.1" ]; then
    echo "[ci-vk] provisioning lavapipe into $PFX (no root)"
    mkdir -p "$PFX" /tmp/vk/deb && cd /tmp/vk/deb
    for p in libvulkan1 mesa-vulkan-drivers; do apt-get download "$p"; done
    for d in *.deb; do dpkg-deb -x "$d" "$PFX/"; done
    cd "$ROOT"
    for cand in "$PFX"/usr/share/vulkan/icd.d/lvp_icd*.json; do
      [ -e "$cand" ] && ICD="$cand" && break
    done
    [ -n "$ICD" ] || { echo "[ci-vk] FATAL: no lavapipe ICD under $PFX/usr/share/vulkan/icd.d/" >&2; exit 1; }
    # Point the manifest at the extracted driver library (its library_path is relative to a system install).
    python3 - "$ICD" "$PFX" <<'PYIN'
import json, sys
icd, pfx = sys.argv[1], sys.argv[2]
d = json.load(open(icd))
d["ICD"]["library_path"] = f"{pfx}/usr/lib/x86_64-linux-gnu/libvulkan_lvp.so"
json.dump(d, open(icd, "w"), indent=2)
PYIN
  fi
  for cand in "$PFX"/usr/share/vulkan/icd.d/lvp_icd*.json; do
    [ -e "$cand" ] && ICD="$cand" && break
  done
  [ -n "$ICD" ] || { echo "[ci-vk] FATAL: no lavapipe ICD under $PFX/usr/share/vulkan/icd.d/" >&2; exit 1; }
  export LD_LIBRARY_PATH="$PFX/usr/lib/x86_64-linux-gnu${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
fi

export VK_ICD_FILENAMES="$ICD"
export VK_DRIVER_FILES="$ICD"
export MEWSTAGE_VK_SMOKE=1
# The gated tests append "N lavapipe tests RAN" here; it is printed at the end so the count is never missed.
export MEWSTAGE_VK_SMOKE_REPORT="${MEWSTAGE_VK_SMOKE_REPORT:-/tmp/mewstage-vk-smoke-report.txt}"
: > "$MEWSTAGE_VK_SMOKE_REPORT"
echo "[ci-vk] lavapipe ICD: $VK_ICD_FILENAMES"

cd "$ROOT"
rc=0
for proj in $TEST_PROJECTS; do
  [ -e "$proj" ] || { echo "[ci-vk] skip (not present): $proj"; continue; }
  echo "[ci-vk] dotnet test $proj"
  "$DOTNET" test "$proj" "$@" || rc=1
done
echo "[ci-vk] ---- Vulkan tests that RAN (a green run with 0 is a setup failure) ----"
cat "$MEWSTAGE_VK_SMOKE_REPORT"
if ! grep -qE "\b[1-9][0-9]* lavapipe tests RAN" "$MEWSTAGE_VK_SMOKE_REPORT"; then
  echo "[ci-vk] FATAL: no lavapipe test ran" >&2
  rc=1
fi
exit $rc
