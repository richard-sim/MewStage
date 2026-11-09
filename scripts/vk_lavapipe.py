#!/usr/bin/env python3
"""Run ONE command with Mesa lavapipe as its only Vulkan driver (Windows).

Usage:
    python scripts/vk_lavapipe.py [--check] -- <command> [args...]
    e.g.  python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Rendering.Tests

What it does:
  * finds the lavapipe restored by `python third_party/tools/restore-tools.py --only mesa_lavapipe` (version and
    platform from tools.lock.toml), locating the driver manifest by GLOB (lvp_icd*.json): mesa has renamed it before;
  * checks vulkan_lvp.dll against the SHA256 pinned in the lock;
  * refuses to run elevated: the Vulkan loader IGNORES VK_DRIVER_FILES / VK_ICD_FILENAMES in an elevated process,
    which would make every MEWSTAGE_VK_SMOKE test silently SKIP;
  * sets, for the CHILD PROCESS ONLY: VK_DRIVER_FILES and VK_ICD_FILENAMES = the manifest (VK_DRIVER_FILES is
    exclusive: the real GPU drivers are not loaded) and MEWSTAGE_VK_SMOKE=1. Nothing global changes; every other
    process keeps the real drivers.
  * --check: also runs `vulkaninfo --summary` (when it is on PATH) under the same environment and fails unless
    llvmpipe is the only device; with no command it only checks.

Exit code: the child's, or 2 for a setup error. Console output is plain ASCII.
On Linux use scripts/ci-vulkan-render-test.sh instead.
"""
from __future__ import annotations

import argparse
import glob
import hashlib
import os
import shutil
import subprocess
import sys

try:
    import tomllib
except ModuleNotFoundError:  # pragma: no cover - Python 3.11+ has it
    print("!! vk_lavapipe: need Python 3.11+ (tomllib)")
    sys.exit(2)

SCRIPTS = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.normpath(os.path.join(SCRIPTS, "..", "third_party", "tools"))
TOOL = "mesa_lavapipe"
PLATFORM = "win-x64"
DLL = "vulkan_lvp.dll"


def fail(msg: str) -> int:
    print("!! vk_lavapipe: " + msg)
    return 2


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def is_elevated() -> bool:
    try:
        import ctypes  # noqa: PLC0415 - Windows only
        return bool(ctypes.windll.shell32.IsUserAnAdmin())  # type: ignore[attr-defined]
    except Exception:  # noqa: BLE001 - not Windows, or no shell32: treat as not elevated
        return False


def resolve() -> tuple[str | None, str | None]:
    """Return (manifest path, error message). Exactly one of the two is None."""
    with open(os.path.join(TOOLS, "tools.lock.toml"), "rb") as f:
        lock = tomllib.load(f)
    spec = lock.get(TOOL)
    if not isinstance(spec, dict) or PLATFORM not in spec:
        return None, f"no [{TOOL}.{PLATFORM}] entry in {os.path.join(TOOLS, 'tools.lock.toml')}"
    folder = os.path.join(TOOLS, TOOL, str(spec.get("version", "v")), PLATFORM)
    restore = f'"{sys.executable}" "{os.path.join(TOOLS, "restore-tools.py")}" --only {TOOL}'
    manifests = sorted(glob.glob(os.path.join(folder, "lvp_icd*.json")))
    if len(manifests) != 1:
        return None, (f"expected exactly one lvp_icd*.json in {folder}, found {len(manifests)}. "
                      f"Restore it with: {restore}")
    dll = os.path.join(folder, DLL)
    if not os.path.isfile(dll):
        return None, f"{dll} is missing. Restore it with: {restore}"
    want = spec[PLATFORM].get("files_sha256", {}).get(DLL, "")
    if not want:
        return None, f"no pinned SHA256 for {DLL} in the lock (files_sha256)"
    got = sha256(dll)
    if got != want:
        return None, f"{DLL} SHA256 mismatch: want {want} got {got}. Delete {folder} and restore again."
    return os.path.abspath(manifests[0]), None


def child_env(manifest: str) -> dict[str, str]:
    env = dict(os.environ)
    # Two separate assignments on purpose: never derive one from the other on the same line.
    env["VK_DRIVER_FILES"] = manifest
    env["VK_ICD_FILENAMES"] = manifest
    env["MEWSTAGE_VK_SMOKE"] = "1"
    env.pop("VK_ADD_DRIVER_FILES", None)  # ignored anyway when VK_DRIVER_FILES is set; remove the ambiguity
    return env


def check(env: dict[str, str]) -> int:
    exe = shutil.which("vulkaninfo")
    if exe is None:
        print("vk_lavapipe: --check: vulkaninfo not on PATH, cannot confirm the device here; "
              "count the MEWSTAGE_VK_SMOKE tests that RAN instead")
        return 0
    r = subprocess.run([exe, "--summary"], env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
    text = r.stdout.decode("utf-8", "replace")
    names = [ln.split("=", 1)[1].strip() for ln in text.splitlines() if ln.strip().startswith("deviceName")]
    print("vk_lavapipe: --check: vulkaninfo devices: " + (", ".join(names) if names else "(none)"))
    if r.returncode != 0 or not names:
        return fail(f"vulkaninfo found no device (exit {r.returncode})")
    if not all("llvmpipe" in n.lower() for n in names):
        return fail("a non-lavapipe device is visible; VK_DRIVER_FILES was not honored")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Run one command with Mesa lavapipe as its only Vulkan driver.")
    ap.add_argument("--check", action="store_true", help="confirm with vulkaninfo that only llvmpipe is visible")
    ap.add_argument("command", nargs=argparse.REMAINDER, help="-- <command> [args...]")
    a = ap.parse_args()
    cmd = a.command[1:] if a.command[:1] == ["--"] else a.command

    if not sys.platform.startswith("win"):
        return fail("Windows only. On Linux use scripts/ci-vulkan-render-test.sh")
    if is_elevated():
        return fail("running elevated (as administrator): the Vulkan loader ignores VK_DRIVER_FILES there and "
                    "every MEWSTAGE_VK_SMOKE test would SKIP. Run from a normal (non-admin) shell.")
    manifest, err = resolve()
    if err or manifest is None:
        return fail(err or "could not resolve the lavapipe manifest")
    env = child_env(manifest)
    print("vk_lavapipe: manifest         " + manifest)
    print("vk_lavapipe: VK_DRIVER_FILES  = " + env["VK_DRIVER_FILES"])
    print("vk_lavapipe: VK_ICD_FILENAMES = " + env["VK_ICD_FILENAMES"])
    print("vk_lavapipe: MEWSTAGE_VK_SMOKE=1")
    if a.check:
        rc = check(env)
        if rc or not cmd:
            return rc
    if not cmd:
        return fail("no command given (usage: vk_lavapipe.py [--check] -- <command> [args...])")
    print("vk_lavapipe: running: " + " ".join(cmd))
    try:
        return subprocess.run(cmd, env=env, check=False).returncode
    except FileNotFoundError:
        return fail("command not found: " + cmd[0])


if __name__ == "__main__":
    sys.exit(main())
