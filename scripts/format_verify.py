#!/usr/bin/env python3
"""Run `dotnet format` over every MewStage project (never the solution: the vendored third_party projects carry their
own editorconfig and are not ours to format) and report the first project that would change.

    python scripts/format_verify.py            # verify (exit 1 when any project is not formatted)
    python scripts/format_verify.py --fix      # apply whitespace + style fixes in place
    python scripts/format_verify.py --dotnet /path/to/dotnet

Two passes per project, as the CI gate runs them: `whitespace`, then `style --severity warn`. The style pass covers the
IDE* rules the build already enforces (warnings are errors), so a clean build and a clean format pass agree.
"""
from __future__ import annotations

import argparse
import pathlib
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
# MewStage.Ui.Backend compiles a few upstream bridge files from third_party/ as linked sources. Formatting them would
# rewrite vendored bytes and break `vendor.py --check`, so the vendored trees are never formatted. The path is
# relative to ROOT, the working directory dotnet format runs in (an absolute path is ignored).
EXCLUDE = ["--exclude", "third_party/"]


def projects() -> list[pathlib.Path]:
    found: list[pathlib.Path] = []
    for folder in ("src", "tests"):
        for csproj in sorted((ROOT / folder).glob("*/*.csproj")):
            found.append(csproj)
    return found


def run(dotnet: str, args: list[str]) -> int:
    proc = subprocess.run([dotnet, "format", *args], cwd=ROOT, capture_output=True, text=True, check=False)
    if proc.returncode != 0:
        # Keep the lines that name a file; the workspace-loading warning is noise.
        for line in (proc.stdout + proc.stderr).splitlines():
            if ".cs(" in line or "error" in line.lower():
                print("    " + line.strip())
    return proc.returncode


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--fix", action="store_true", help="apply the fixes instead of verifying")
    parser.add_argument("--dotnet", default="dotnet", help="the dotnet executable (default: dotnet on PATH)")
    opts = parser.parse_args()

    verify = [] if opts.fix else ["--verify-no-changes"]
    failed: list[str] = []
    for csproj in projects():
        rel = csproj.relative_to(ROOT).as_posix()
        print(f"[format] {rel}")
        rc = run(opts.dotnet, ["whitespace", str(csproj), *EXCLUDE, *verify])
        rc |= run(opts.dotnet, ["style", str(csproj), "--severity", "warn", *EXCLUDE, *verify])
        if rc != 0:
            failed.append(rel)
    if failed:
        verb = "could not be formatted" if opts.fix else "would change"
        print(f"[format] FAIL: {len(failed)} project(s) {verb}: " + ", ".join(failed))
        return 1
    print(f"[format] OK: {len(projects())} projects " + ("formatted" if opts.fix else "verified"))
    return 0


if __name__ == "__main__":
    sys.exit(main())
