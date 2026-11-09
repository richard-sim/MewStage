#!/usr/bin/env python3
"""Restore and SHA256-verify the pinned native tools listed in tools.lock.toml.

Each tool's per-platform artifact is downloaded into third_party/tools/<tool>/<version>/<platform>/ and its SHA256
is checked against the lock. An empty hash in the lock means "not pinned yet": the artifact is fetched and its hash
printed so a maintainer can paste it into the lock; a mismatch fails.

Optional tools (`optional = true` in the lock, e.g. mesa_lavapipe) are skipped unless named with --only or
--include-optional is given.

A platform entry may carry `extract = [...]` (archive member paths) and `files_sha256 = { name = hash }`: then ONLY
those members are extracted, flattened into the platform folder, and each extracted file is SHA256-checked. A .7z
archive needs the 7-Zip command line (7z / 7zz / 7za on PATH, or the standard Windows install folder); py7zr is not
used because it cannot decode the BCJ2 filter that mesa-dist-win archives use.

Usage:  python third_party/tools/restore-tools.py [--platform win-x64|linux-x64|osx-arm64] [--only dxc,mesa_lavapipe]
                                                  [--include-optional]

Console output is plain ASCII. Exit code 0 on success, 1 on a download / hash / extraction failure, 2 on usage.
"""
from __future__ import annotations

import argparse
import hashlib
import os
import platform as _platform
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.request
import zipfile

try:
    import tomllib
except ModuleNotFoundError:  # pragma: no cover - Python 3.11+ has it
    print("!! restore-tools: need Python 3.11+ (tomllib)")
    sys.exit(2)

ROOT = os.path.dirname(os.path.abspath(__file__))

SEVEN_ZIP_NAMES = ("7z", "7zz", "7za")
SEVEN_ZIP_WINDOWS = (r"C:\Program Files\7-Zip\7z.exe", r"C:\Program Files (x86)\7-Zip\7z.exe")


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def detect_platform() -> str:
    s, m = sys.platform, _platform.machine().lower()
    if s.startswith("win"):
        return "win-x64"
    if s == "darwin":
        return "osx-arm64" if m in ("arm64", "aarch64") else "osx-x64"
    return "linux-x64"


def find_7zip() -> str | None:
    for n in SEVEN_ZIP_NAMES:
        p = shutil.which(n)
        if p:
            return p
    for p in SEVEN_ZIP_WINDOWS:
        if os.path.isfile(p):
            return p
    return None


def files_ok(dest_dir: str, files_sha256: dict[str, str]) -> bool:
    """True when every expected file exists in dest_dir with its pinned hash."""
    if not files_sha256:
        return False
    for name, want in files_sha256.items():
        p = os.path.join(dest_dir, name)
        if not os.path.isfile(p) or sha256(p) != want:
            return False
    return True


def extract_members(tool: str, fname: str, dest_dir: str, members: list[str]) -> bool:
    """Extract only `members` from the archive, flattened into dest_dir. Returns True on success."""
    if fname.endswith(".7z"):
        exe = find_7zip()
        if exe is None:
            print(f"!! {tool}: a .7z archive needs the 7-Zip command line. Install 7-Zip (https://www.7-zip.org) "
                  f"or put 7z / 7zz on PATH, then re-run.")
            return False
        with tempfile.TemporaryDirectory(dir=dest_dir) as tmp:
            r = subprocess.run([exe, "e", "-y", f"-o{tmp}", fname, *members],
                               stdout=subprocess.PIPE, stderr=subprocess.STDOUT, check=False)
            if r.returncode != 0:
                print(f"!! {tool}: 7-Zip exit {r.returncode}")
                return False
            for m in members:
                src = os.path.join(tmp, os.path.basename(m))
                if not os.path.isfile(src):
                    print(f"!! {tool}: member not found in archive: {m}")
                    return False
                os.replace(src, os.path.join(dest_dir, os.path.basename(m)))
        return True
    if fname.endswith(".zip"):
        with zipfile.ZipFile(fname) as z:
            for m in members:
                with z.open(m) as src, open(os.path.join(dest_dir, os.path.basename(m)), "wb") as dst:
                    shutil.copyfileobj(src, dst)
        return True
    if fname.endswith((".tar.gz", ".tgz", ".tar.bz2", ".tar.xz")):
        with tarfile.open(fname) as t:
            for m in members:
                src = t.extractfile(m)
                if src is None:
                    print(f"!! {tool}: member not found in archive: {m}")
                    return False
                with open(os.path.join(dest_dir, os.path.basename(m)), "wb") as dst:
                    shutil.copyfileobj(src, dst)
        return True
    print(f"!! {tool}: cannot extract members from {os.path.basename(fname)}")
    return False


def extract_all(fname: str, dest_dir: str) -> None:
    if fname.endswith(".zip"):
        with zipfile.ZipFile(fname) as z:
            z.extractall(dest_dir)
    elif fname.endswith((".tar.gz", ".tgz", ".tar.bz2", ".tar.xz")):
        with tarfile.open(fname) as t:
            if hasattr(tarfile, "data_filter"):
                t.extractall(dest_dir, filter="data")
            else:  # pragma: no cover - Python < 3.12
                t.extractall(dest_dir)


def restore_one(tool: str, spec: dict, plat_name: str) -> int:
    plat = spec.get(plat_name)
    if plat is None:
        print(f"[skip] {tool}: no {plat_name} entry")
        return 0
    url = plat.get("url", "")
    dest_dir = os.path.join(ROOT, tool, str(spec.get("version", "v")), plat_name)
    os.makedirs(dest_dir, exist_ok=True)
    members = plat.get("extract", [])
    files_sha = plat.get("files_sha256", {})
    if members and files_ok(dest_dir, files_sha):
        print(f"[ok  ] {tool}: already restored, {len(files_sha)} file(s) verified")
        return 0
    fname = os.path.join(dest_dir, os.path.basename(url.split("?")[0]))
    if not os.path.exists(fname):
        print(f"[get ] {tool} <- {url}")
        try:
            urllib.request.urlretrieve(url, fname)
        except Exception as e:  # noqa: BLE001 - report any download failure the same way
            print(f"!! {tool}: download failed: {e}")
            return 1
    got = sha256(fname)
    want = plat.get("sha256", "")
    if not want:
        print(f"[warn] {tool}: unpinned. sha256 = \"{got}\"  (paste into tools.lock.toml)")
    elif got != want:
        print(f"!! {tool}: sha256 mismatch want={want} got={got} (delete {fname} and re-run)")
        return 1
    else:
        print(f"[ok  ] {tool}: archive verified")
    if members:
        if not extract_members(tool, fname, dest_dir, members):
            return 1
        bad = [n for n, w in files_sha.items()
               if not os.path.isfile(os.path.join(dest_dir, n)) or sha256(os.path.join(dest_dir, n)) != w]
        if bad:
            print(f"!! {tool}: extracted file hash mismatch: {', '.join(bad)}")
            return 1
        print(f"[ok  ] {tool}: extracted {len(members)} file(s), {len(files_sha)} hash-verified -> {dest_dir}")
        return 0
    extract_all(fname, dest_dir)
    exe = plat.get("exe")
    if exe:
        exe_path = os.path.join(dest_dir, exe)
        if not os.path.isfile(exe_path):
            print(f"!! {tool}: expected executable missing after extraction: {exe_path}")
            return 1
        if not sys.platform.startswith("win"):
            os.chmod(exe_path, 0o755)
        print(f"[ok  ] {tool}: {exe_path}")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Restore and verify the pinned native tools (tools.lock.toml).")
    ap.add_argument("--platform", default=detect_platform(), help="win-x64 | linux-x64 | osx-arm64")
    ap.add_argument("--only", default="", help="comma-separated tool names; optional tools are allowed here")
    ap.add_argument("--include-optional", action="store_true", help="also restore tools marked optional")
    args = ap.parse_args()
    only = {t for t in args.only.split(",") if t}

    with open(os.path.join(ROOT, "tools.lock.toml"), "rb") as f:
        lock = tomllib.load(f)

    unknown = only - {k for k, v in lock.items() if isinstance(v, dict)}
    if unknown:
        print(f"!! unknown tool(s): {', '.join(sorted(unknown))}")
        return 2

    rc = 0
    for tool, spec in lock.items():
        if tool == "schema" or not isinstance(spec, dict):
            continue
        if only and tool not in only:
            continue
        if spec.get("optional") and not only and not args.include_optional:
            print(f"[skip] {tool}: optional (use --only {tool} or --include-optional)")
            continue
        rc = max(rc, restore_one(tool, spec, args.platform))
    return rc


if __name__ == "__main__":
    sys.exit(main())
