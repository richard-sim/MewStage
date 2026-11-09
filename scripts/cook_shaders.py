#!/usr/bin/env python3
"""Cook the HLSL shaders listed in assets/shaders/shaders.manifest.toml.

Two modes, chosen by whether --dxc is given:
  * DXC mode (``--dxc <path to dxc>``): the production cook. Each entry point is compiled to DXIL (DX12, ``-Zpr``)
    and to SPIR-V (Vulkan, ``-spirv -fvk-use-dx-layout -Zpr``).
  * glslang mode (no --dxc): a cross-check for machines without DXC. Each entry point is compiled HLSL -> SPIR-V
    with glslang and validated with spirv-val (an external spirv-val when one is on PATH, else glslang's built-in
    validator). No DXIL is produced in this mode.

Output: ``<out>/<file>.<entry>.dxil`` and ``<out>/<file>.<entry>.spv`` (default out: ``<shaders>/cooked``; the
shader folder defaults to assets/shaders and ``--shaders <dir>`` points at another one with its own manifest, which
is how the test projects cook their fixtures). The cooked blobs are committed, so a fresh clone needs no compiler;
re-run this after editing a shader.

Get the pinned standalone DXC with ``python third_party/tools/restore-tools.py --only dxc`` (the Windows SDK copy of
dxc.exe has no SPIR-V code generation; this script detects that, emits DXIL only, warns once and still succeeds).

Matrix packing: the C# side uploads System.Numerics matrices row-major with no transpose and the shaders use
mul(v, M); ``-Zpr`` packs matrices row-major so DXC reads those bytes correctly. glslang's default packing with
mul(v, M) computes the same product for that upload, so no per-shader pragma is used.

Console output is plain ASCII. Exit code 0 when every entry cooked, 1 on any failure, 2 on usage.
"""
from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
import tomllib
from pathlib import Path

_DXC_PROFILE = {"vert": "vs_6_6", "frag": "ps_6_6", "comp": "cs_6_6"}

# The manifest's schema version this cook reads (its top-level `schema` key).
_MANIFEST_SCHEMA = 1


def _run(cmd: list[str]) -> tuple[int, str]:
    p = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, check=False)
    return p.returncode, p.stdout


def entries(manifest: Path):
    """(file, entry, stage) per [[shader]] table. A malformed manifest is an error, never a silent skip."""
    with manifest.open("rb") as f:
        data = tomllib.load(f)
    if data.get("schema") != _MANIFEST_SCHEMA:
        raise SystemExit(f"{manifest}: schema {data.get('schema')!r}, this cook reads {_MANIFEST_SCHEMA}")
    shaders = data.get("shader")
    if not isinstance(shaders, list) or not shaders:
        raise SystemExit(f"{manifest}: no [[shader]] tables")
    for i, s in enumerate(shaders):
        missing = [k for k in ("file", "entry", "stage") if not isinstance(s.get(k), str) or not s.get(k)]
        if missing:
            raise SystemExit(f"{manifest}: [[shader]] #{i + 1} is missing {', '.join(missing)}")
        if s["stage"] not in _DXC_PROFILE:
            raise SystemExit(f"{manifest}: [[shader]] #{i + 1}: unknown stage '{s['stage']}'")
        yield s["file"], s["entry"], s["stage"]


def cook_dxc(sh_dir: Path, out: Path, dxc: str, dxil_only: bool) -> int:
    dxil_ok = spv_ok = fails = 0
    spirv = not dxil_only          # auto-disabled when this DXC has no SPIR-V code generation
    for file, entry, stage in entries(sh_dir / "shaders.manifest.toml"):
        src = sh_dir / file
        profile = _DXC_PROFILE[stage]
        dxil = out / f"{file}.{entry}.dxil"
        spv = out / f"{file}.{entry}.spv"
        rc, o = _run([dxc, "-T", profile, "-E", entry, "-Zpr", "-Fo", str(dxil), str(src)])
        if rc != 0:
            print(f"FAIL dxil   {file}::{entry} (exit {rc})")
            for ln in o.splitlines()[:6]:
                print(f"    {ln}")
            fails += 1
            continue
        dxil_ok += 1
        if spirv:
            rc, o = _run([dxc, "-T", profile, "-E", entry, "-spirv", "-Zpr", "-fvk-use-dx-layout",
                          "-Fo", str(spv), str(src)])
            if rc != 0:
                if "SPIR-V CodeGen not available" in o:
                    print("NOTE: this DXC has no SPIR-V code generation; emitting DXIL only. For the Vulkan "
                          "backend use the standalone DXC (third_party/tools/restore-tools.py --only dxc).")
                    spirv = False
                else:
                    print(f"FAIL spirv  {file}::{entry} (exit {rc})")
                    for ln in o.splitlines()[:6]:
                        print(f"    {ln}")
                    fails += 1
                    continue
            else:
                spv_ok += 1
        print(f"dxc     {file}::{entry}")
    print(f"cooked {dxil_ok} DXIL + {spv_ok} SPIR-V -> {out} (failures: {fails})")
    return 1 if fails else 0


def cook_glslang(sh_dir: Path, out: Path, glslang: str, spirv_val: str, target: str) -> int:
    ok = fails = 0
    exe = shutil.which(glslang)
    if exe is None:
        print(f"glslang not found ('{glslang}'); install glslang-tools or pass --dxc", file=sys.stderr)
        return 2
    external_val = shutil.which(spirv_val)
    for file, entry, stage in entries(sh_dir / "shaders.manifest.toml"):
        src = sh_dir / file
        spv = out / f"{file}.{entry}.spv"
        cmd = [exe, "-D", "-e", entry, "-S", stage, "--target-env", target, "-I" + str(sh_dir)]
        if external_val is None:
            cmd.append("--spirv-val")
        cmd += ["-o", str(spv), str(src)]
        rc, o = _run(cmd)
        if rc != 0:
            print(f"FAIL glslang {file}::{entry} (exit {rc})")
            for ln in o.splitlines()[:6]:
                print(f"    {ln}")
            fails += 1
            continue
        if external_val is not None:
            rc, o = _run([external_val, "--target-env", target, str(spv)])
            if rc != 0:
                print(f"FAIL spirv-val {file}::{entry} (exit {rc})")
                for ln in o.splitlines()[:6]:
                    print(f"    {ln}")
                fails += 1
                continue
        ok += 1
        print(f"glslang {file}::{entry}")
    print(f"cooked {ok} SPIR-V (glslang cross-check, no DXIL) -> {out} (failures: {fails})")
    return 1 if fails else 0


def main() -> int:
    ap = argparse.ArgumentParser(description="Cook MewStage HLSL shaders to DXIL + SPIR-V (DXC) or SPIR-V (glslang).")
    ap.add_argument("out", nargs="?", default="", help="output folder (default: <shaders>/cooked)")
    ap.add_argument("--shaders", default="",
                    help="folder holding shaders.manifest.toml and the HLSL (default: assets/shaders)")
    ap.add_argument("--dxc", default=os.environ.get("DXC", ""),
                    help="path to the dxc executable (production: DXIL + SPIR-V). Without it, glslang cross-check.")
    ap.add_argument("--glslang", default=os.environ.get("GLSLANG", "glslangValidator"))
    ap.add_argument("--spirv-val", default=os.environ.get("SPIRV_VAL", "spirv-val"))
    ap.add_argument("--target", default=os.environ.get("VK_TARGET", "vulkan1.1"),
                    help="glslang / spirv-val target environment (default vulkan1.1)")
    ap.add_argument("--dxil-only", action="store_true", help="DXC mode: skip SPIR-V (DX12 only)")
    a = ap.parse_args()
    sh_dir = Path(a.shaders).resolve() if a.shaders else (Path(__file__).resolve().parent.parent / "assets" / "shaders").resolve()
    if not (sh_dir / "shaders.manifest.toml").is_file():
        print(f"no shaders.manifest.toml in {sh_dir}", file=sys.stderr)
        return 2
    out = Path(a.out).resolve() if a.out else sh_dir / "cooked"
    out.mkdir(parents=True, exist_ok=True)
    if a.dxc:
        if not Path(a.dxc).exists():
            print(f"DXC not found at '{a.dxc}' (python third_party/tools/restore-tools.py --only dxc)", file=sys.stderr)
            return 2
        return cook_dxc(sh_dir, out, a.dxc, a.dxil_only)
    return cook_glslang(sh_dir, out, a.glslang, a.spirv_val, a.target)


if __name__ == "__main__":
    raise SystemExit(main())
