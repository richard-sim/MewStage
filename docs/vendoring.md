# Vendoring

MewUI, MewVG and three of MewUI's extensions are vendored as source under `third_party/`, pinned to one upstream
commit each and patched. Nothing here is needed to build from a fresh clone (the vendored tree is committed); this
page is for whoever has to update it.

## Why source, and why patches

The UI backend compiles against MewUI and MewVG internals, which has no public API. Upstream's own backends do the
same through `InternalsVisibleTo`; MewStage does it through grants kept in small files inside the vendored trees that
the updater never overwrites (`preserve` in the lock). That alone needs source rather than packages. The patches are
the second reason: an embedded host asks things of MewUI that its platform hosts never do (several hosts in one
process, a surface drawn below scale 1, zero allocation in a steady frame), and a few of those needed changes in the
framework itself.

Every patch is in `third_party/patches/<repo>/<category>/NNNN-<name>.patch`, with a header paragraph that says what
it changes and why. `third_party/NOTICE` repeats the paragraph for every patch, and the lock records which patches
apply to which component in which order.

## The lock

`third_party/vendor.lock.toml` is the single record of provenance.

- `[sources.*]`: one upstream commit per repository, never a branch or a tag, with the SHA256 of the codeload tarball
  and a content hash over every file in it. Submodules upstream uses (SvgNet under MewUI.Svg, Clipper2 under
  MewUI.Geometry, LiveCharts2 under MewUI.MewCharts) are sources of their own, pinned to the commit the parent
  records.
- `[components.*]`: one upstream folder mapped to one `third_party` folder, with include and exclude globs, the
  preserved files, and the ordered list of patches with their categories. `third_party/MewUI` and `third_party/MewVG`
  mirror the upstream repository layout (`build/`, `src/<project>/`, `extensions/<project>/`), so upstream's project
  files, their relative links into `src/MewUI/Shared/` and the `build/` props work unmodified. MewStage hooks in only
  through upstream's own local-override file (`build/MewUI.Local.props`, and the same hook added to MewVG by the one
  `mewvg/build` patch), which restricts the vendored projects to net10.0.
- `[categories.*]`: the patch categories, each mandatory or optional, with the one-line reason.

The pins at the time of writing: MewUI `8b9c9433` (main, 2026-10-16, version 0.22.1+), MewVG `5758c6bf` (tag
v0.10.1, the version MewUI main builds against), SvgNet `853a9ae8`, Clipper2 `21ebba05`, LiveCharts2 `1cab5f34`.

## The patch categories

| Category | Mandatory | What it does |
| --- | --- | --- |
| `core-bridge` | yes | A MewStage branch of the shared MewVG `IGraphicsContext` bridge: the backend supplies a `MewVGContext` over the app's GPU device, group opacity composites inside the one UI pass, and the GL-only clear and blit path is compiled out. The backend does not compile without it. |
| `design-space` | yes | A window frame drawn to a surface whose scale is below 1 derives its client DIPs from that scale. The clamp at 1 made a 1280 x 720 window showing a 1920 x 1080 layout clip a third of it. |
| `multi-host` | yes | Per-thread UI statics (the tree-walk scratch lists), a per-thread graphics factory and a per-thread animation manager, so several embedded hosts run in one process (parallel test classes, secondary windows). |
| `build` | yes | The local-override hook in MewVG's build props, so the vendored MewVG builds net10.0 only. |
| `allocation` | no | Six patches that remove per-frame allocations from the steady state (text replay delegates, gradient LUT lookups, cache eviction closures, the scene pass, template wiring, dashed strokes). Skipping them makes a frame allocate and the allocation gates fail, as expected. |
| `text` | no | Letter spacing drawn per grapheme, a negative half-leading, and a font registry that keeps every face of a family (the Kit draws every weight of Lora on the platform host because of the last one). |
| `rendering` | no | Rectangle strokes keep antialiasing when the transform takes them off the pixel grid. |
| `mewdock` | no | MewDock tool groups can hide their caption bar. |

A mandatory category cannot be skipped by the script. The optional ones can (`--skip-category allocation,text`), and
`--only-mandatory` vendors with the mandatory set alone; `--check` then reports which categories the tree carries.
The point of the split is honesty about what the demo needs from upstream and what it merely prefers.

## The script

`third_party/tools/vendor.py` downloads the codeload tarball of each pinned commit into `third_party/.vendor-cache`
(gitignored), verifies both hashes, reads it in memory (no extraction, no git, no `patch` binary), writes the
components, and applies the patches with a strict pure-Python applier that fails loudly when a hunk no longer
matches, which is the signal that upstream moved the patched code. The applied patch set is recorded in
`third_party/.vendor-state.toml`.

    python third_party/tools/vendor.py --check              # the tree equals the lock + the recorded patch set (exit 1 on drift)
    python third_party/tools/vendor.py                      # re-vendor every component, every patch
    python third_party/tools/vendor.py --list               # sources and components
    python third_party/tools/vendor.py --list-patches       # every patch, its category and purpose
    python third_party/tools/vendor.py --tarball-dir <dir>  # offline: <repo>-<commit8>.tar.gz files from a folder
    python third_party/tools/vendor.py --emit-svg-generated # rebuild MewUI.Svg/Generated (needs dotnet)
    python third_party/tools/vendor.py --self-test          # the patch applier's own tests

CI runs `--self-test` and `--check` on every push (the `vendor-lock` job). `MewStage.ThirdParty.Tests` holds one
smoke test per vendored extension's entry point (MewDock, Svg, Geometry, MewCharts, Preview).

## Procedures

**Re-pin to a newer upstream commit.** Change the commit in `[sources.<repo>]` and run the script. It downloads the
new tarball and stops with a content mismatch that prints the digest it computed; paste that into `content_sha256`,
run again, and it notes the archive hash to paste into `archive_sha256`. Then it applies the patches, and any patch
whose hunks no longer match stops the run with the file and hunk. Rebase that patch (edit the vendored file by hand
to the intended state, regenerate the patch with `diff -u` against a clean copy of the upstream file, keep the
header paragraph), run again, then `--check`, build, and run the suites. The allocation gates and the lavapipe
contract tests are the ones that notice an upstream behavior change.

**Add a patch.** Make the change in the vendored file. Produce a unified diff against the unpatched upstream file
(the cache tarball has it) with paths relative to the component's destination, `-p1` style. Write the header
paragraph: what it changes, why, and "no behavioral change" or what the change is. Put it in the category folder
with the next number, add it to the component's patch list in the lock in the order it must apply (a patch that
touches a file another patch touches says which one it applies after, in its header), add the paragraph to
`third_party/NOTICE`, and run `--check`.

**Add a component.** Add the source (if it is a new repository) and a `[components.<name>]` entry: `src_root`, `dest`,
include and exclude globs, preserved files, and its place in `[meta].order`. A component whose destination lies
inside another's owns that folder; the outer one never touches it. Add the project to `MewStage.sln`, to the layering
test's allowed vendored references, and a smoke test to `MewStage.ThirdParty.Tests`.

**Consumer wiring.** A project that uses MewUI (the executable, a test host) imports `third_party/MewUI.Consumer.targets`
at its end with `MewStageUiHost` set to `Embedded` (no OS windows of MewUI's own: DevTools, Hot Reload, managed file
dialogs and touch off unless the project turns them on) or `Tool` (upstream's defaults, which the demo uses so the
DevTools work in Debug). The targets file gives a project reference what a NuGet consumer gets from the package: the
binding-path source generator and upstream's feature switches.

## Native tools

`third_party/tools/restore-tools.py` fetches the pinned native tools from `tools.lock.toml` into
`third_party/tools/<tool>/<version>/<platform>/` (gitignored) and verifies their hashes: the DirectX Shader Compiler
(to re-cook the shaders; the standalone release, since the Windows SDK copy has no SPIR-V output) and, optionally,
Mesa's lavapipe for running the Vulkan tests on a Windows machine without a GPU (docs/testing.md). The committed
shader blobs mean a fresh clone needs neither.
