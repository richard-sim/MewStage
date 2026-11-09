# Tools

Two scripts live here. Neither is needed to build or run MewStage from a fresh clone: the vendored MewUI / MewVG
sources and the cooked shader blobs are committed.

## `vendor.py`: re-vendor MewUI, MewVG and their extensions

Re-vendors the upstream sources from `../vendor.lock.toml` (pinned commits, SHA256-verified codeload tarballs read
in memory, a pure-Python patch applier; no git, no `patch` binary). `--check` verifies the tree against the lock and
the recorded patch set in `../.vendor-state.toml`; `--self-test` runs the patch applier's own tests.

```
python third_party/tools/vendor.py --check              # the tree matches the lock + the applied patch set
python third_party/tools/vendor.py                      # re-vendor, every patch category
python third_party/tools/vendor.py --only-mandatory     # re-vendor with the mandatory categories only
python third_party/tools/vendor.py --skip-category allocation,text
python third_party/tools/vendor.py --list-patches       # every patch: category, component, purpose
python third_party/tools/vendor.py --tarball-dir <dir>  # offline: <repo>-<commit8>.tar.gz files
python third_party/tools/vendor.py --emit-svg-generated # rebuild MewUI.Svg/Generated (needs dotnet)
python third_party/tools/vendor.py --self-test
```

See `docs/vendoring.md` for the patch categories and how to add or re-pin one.

## `restore-tools.py`: fetch the pinned native tools

Downloads the tools listed in `tools.lock.toml` (exact versions, per-platform URLs, SHA256) into
`third_party/tools/<tool>/<version>/<platform>/` (gitignored) and verifies them.

```
python third_party/tools/restore-tools.py                       # the mandatory tools for this platform (dxc)
python third_party/tools/restore-tools.py --only dxc
python third_party/tools/restore-tools.py --only mesa_lavapipe  # optional: software Vulkan for the tests on Windows
```

| Tool | Used by | Notes |
|---|---|---|
| `dxc` 1.9.2602.24 | `scripts/cook_shaders.py --dxc <path>` | HLSL to DXIL (DX12) and SPIR-V (Vulkan). The standalone release, not the Windows SDK copy (that one has no SPIR-V code generation). The Linux binary needs glibc 2.38 or newer. |
| `mesa_lavapipe` 26.2.4 | `scripts/vk_lavapipe.py` | Optional. Only `vulkan_lvp.dll` and `lvp_icd.x86_64.json` are extracted and hash-checked. Needs the 7-Zip command line (the archive uses BCJ2). Never referenced by a build; never shipped. |

A platform entry with `extract = [...]` and `files_sha256` extracts only those archive members, flattened, and
verifies each one.
