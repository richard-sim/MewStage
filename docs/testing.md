# Testing

Five test projects, two kinds of test. Headless tests run anywhere with nothing but the SDK: the render graph, the
scheduler on a headless device, the UI host's bookkeeping, the input maps, the demo's view models and bindings.
Rendered tests draw real pixels through the real path (SDL's offscreen video driver, a headless Vulkan device,
`OffscreenSwapchain`, the render graph) and read them back; they need a Vulkan implementation and use Mesa's
lavapipe, a software one, so they run on a machine with no GPU and on CI.

## The suites

| Project | What it covers |
| --- | --- |
| `MewStage.Rendering.Tests` | render graph compile order, barriers per physical slot, transient aliasing, culling, imports, the 0 B steady-state frame; the scheduler, upload ring and frame contexts on the headless device; state and format maps on both backends; DX12 descriptor sizing; camera conventions. Lavapipe: device creation, clear and readback, a triangle through graph and scheduler with a transient depth, a golden fingerprint and determinism. Windows only: the DX12 smoke on the WARP software adapter. |
| `MewStage.Ui.Tests` | the embedded host headless (attach and prune, resize metrics, the OnRequest render decision, the allocation gates), the thread's dispatcher and the pulse (one dispatcher per UI thread shared by its hosts and unbound by the last, `DispatcherTimer` on the frame clock, posted work at the next pump, a focused TextBox's caret blink renders an idle OnRequest host, an application-wide clock renders until it completes, pseudo-handles unique across threads), the navigator (including a vertical slider stepped with Up / Down), fonts (face matching, metrics from the TTF tables, measured width equals drawn width), the input bridge's edge diffing, the external-texture registry, the scratch surfaces (a cache gets one with a zero-copy image, a vector image rasterizes once and blits after, a scratch render outside the `ui` pass throws, a `BitmapCache` renders its subtree once) and `Window.Close()` (Closed raised, the window under it activated, a cancelling `Closing` handler, a closed window refused by `SetWindows`, a pruned window still closes, a closed window's icon cache returns to the pool and a trim frees it). Lavapipe: the host contract (a golden window edge to edge, design space below scale 1, ComboBox in-surface, opacity composites, resize, the window stack through a transparent modal, LOAD keeps a world pass under the scrim) and the renderer gates (a concave fill is a triangle list, the clip edge within half a pixel, a 2400-point path as a mask fill, gradient / image brushes, stencil clip confinement, a translucent concave fill is one even coat, a one-sided border paints exactly its row) and the scratch surfaces (cached and direct pixels match, two caches filled in one frame both survive, a `BitmapCache` subtree matches the uncached one). |
| `MewStage.Host.Tests` | the SDL scancode map (every key reachable once), text append, the pump's event translation, gamepad bookkeeping, `UiLayer`'s scale rule in both spaces, `AssetPaths`, backend selection, cooked shader loading; the layering gate and the no-foreign-references hygiene gate. Lavapipe, through the real `AppHost`: a label renders, pushed events route through the pump and a close ends the loop, Overlay LOAD keeps a world pass under a transparent window, a secondary window opens, presents and closes, a display-scale override lays out at the DIP size. |
| `MewStage.Demo.Tests` | headless: the command line, the demo styles and templates without an `Application`, the HUD's bindings and transition, the pause menu's commands, text input and gamepad engage model, viewport projection and the compositor's bookkeeping, the Workbench's layout JSON, view model, inspector and outliner, the Kit page's tabs, the geometry sample's areas, the DevTools gate in both configurations. Lavapipe journeys through the real demo offscreen at 1280 x 720: the start page, Overlay (HUD, pause, dialog, ComboBox, text, gamepad, letterbox), the Workbench (viewport as external image, two viewports, resize, layout save and restore, the stats window, gestures, inspector and About, the DevTools keys). Leaving a mode closes its windows: the journeys check the closed handles, the Workbench's icon caches going back to the pool and a trim emptying it. Each journey can dump PNGs. |
| `MewStage.ThirdParty.Tests` | one smoke test per vendored extension's entry point: MewDock, Svg, Geometry, MewCharts, Preview. |

The pixel assertions are structural: region means, counts of pixels above a threshold, a corner against a center.
There are no exact goldens, so a font hinting difference between machines does not break a test, while a missing
cube or a clip that shrank by a pixel does.

## Running

    dotnet build MewStage.sln -c Debug
    dotnet test MewStage.sln --no-build -c Debug          # every headless test; the rendered ones skip

A skipped rendered test is as green as a passing one, which is the trap this project guards against: every lavapipe
suite prints how many of its gated tests RAN (`[lavapipe] N lavapipe tests RAN in <project>`), and the CI script
fails a run in which none did. Read that line, not only the color.

**Linux.** Install Mesa's Vulkan driver (`apt install libvulkan1 mesa-vulkan-drivers`) and run

    bash scripts/ci-vulkan-render-test.sh dotnet --no-build -c Debug

The script finds the lavapipe ICD by glob (mesa has renamed the manifest file between releases), points the Vulkan
loader at it, sets `MEWSTAGE_VK_SMOKE=1`, runs the four suites that have rendered tests, and prints the RAN count per
suite. Without root it downloads and extracts the two packages into a prefix instead. The CI workflow runs it with
`MEWSTAGE_VK_VALIDATION=1` as well.

**Windows.** Restore lavapipe once (`python third_party/tools/restore-tools.py --only mesa_lavapipe`; it needs the
7-Zip command line), then run one command under it:

    python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Demo.Tests

The wrapper sets the driver variables for the child process only, so the real GPU drivers are untouched for everything
else, and it refuses to run elevated, because the Vulkan loader ignores those variables in an elevated process and
every gated test would skip in silence.

**Journey dumps.** `MEWSTAGE_JOURNEY_OUT=<dir>` makes the Demo journeys write a PNG of each scene they assert on
(`overlay-hud`, `overlay-pause`, `workbench`, `workbench-two`, `workbench-about`, and so on). The README's screenshots
come from these dumps and from the Kit smoke below.

**Vulkan validation layer.** With the Khronos layer installed (`apt install vulkan-validationlayers`, or the Vulkan
SDK on Windows), `MEWSTAGE_VK_VALIDATION=1` enables it and a debug messenger on every device the tests create; the
console shows `[vk] validation layer + debug messenger ENABLED` once per device and any finding as a line starting
with `[VK-VALIDATION`. Every suite runs clean (0 messages), and CI fails if one appears. Two findings this caught
during development are recorded in `docs/hand-test.md` (Phase 6).

**Kit mode without a display.** `scripts/kit-smoke-x11.sh` runs the Debug demo with `--standard` on an Xvfb screen
(MewUI's X11 platform host, Mesa's software GL), clicks through every tab, toggles each DevTool by its shortcut, counts
the tool windows by title and fails if the process dies (docs/devtools.md).

## Gates that are tests

- **Layering** (`Host.Tests/Architecture/LayeringTests`): reads every project's `ProjectReference` and
  `PackageReference` closure and fails on a reference the layering (docs/architecture.md) does not allow.
- **No foreign references** (`Host.Tests/Hygiene/NoForeignReferencesTests`): walks every text file in the repository
  outside the upstream vendored content and fails on a list of tokens that must not appear.
- **Allocation** (`Ui.Tests`, `Rendering.Tests`): an idle OnRequest frame adds no pass and allocates 0 B; a
  steady-state Continuous frame with text, gradients, images and dashed strokes allocates 0 B; the render graph's
  steady state is 0 B. Measured with `GC.GetAllocatedBytesForCurrentThread`, tiered compilation off.
- **DevTools gate** (`Demo.Tests/Headless/KitTests`): `#if DEBUG` asserts the tools are present and work on the
  embedded host; `#else` asserts they are absent. Run the Demo tests in both configurations when touching the gate.
- **Format** (`scripts/format_verify.py`): `dotnet format whitespace` and `dotnet format style --severity warn` with
  `--verify-no-changes` over every MewStage project, never the solution (the vendored projects are not ours to
  format). `--fix` applies instead of verifying.
- **Vendor lock** (`third_party/tools/vendor.py --check`): the vendored tree equals the lock plus the recorded patch
  set; CI's `vendor-lock` job.

## Hand tests

What could not be run where the code was built (a Windows GPU, a real second monitor, a controller, the Windows AOT
publish) is in `docs/hand-test.md`, one block per phase, each item with what to run and what to expect. It is kept
current: an item stays until it has been run.

## Re-cooking the shaders

The cooked DXIL and SPIR-V blobs in `assets/shaders/cooked` are committed and loaded by name
(`CookedShaders.LoadStage`). To re-cook after editing a `.hlsl`:

    python third_party/tools/restore-tools.py --only dxc
    python scripts/cook_shaders.py --dxc third_party/tools/dxc/<version>/<platform>/bin/<dxc>

The manifest `assets/shaders/shaders.manifest.toml` lists the stages and entry points. The script has a glslang mode
(SPIR-V only) for machines where the pinned DXC binary does not run.
