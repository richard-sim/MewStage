# Hand-test checklist

Checks that need a real machine (a Windows GPU, a desktop, a controller) and so were not run where the code was
built. Each phase appends its own block; a check stays until it has been run and recorded. Format per item: what to
test, where, what to expect, and whether it is new or a fix.

Conventions: run everything from the repository root; `python` is Python 3.11 or newer.

## Phase 2: GPU stack

### DX12 smoke test (WARP)

- **What:** the DX12 backend draws the triangle fixture through the render graph and scheduler with a transient
  depth attachment, on the WARP software adapter, and reads it back.
- **Where:** `dotnet test tests/MewStage.Rendering.Tests` on Windows. The test is
  `Dx12SmokeTests.Dx12_Warp_DrawsTriangle_ThroughGraphAndScheduler`; it skips off Windows.
- **Expected:** the test passes; the console shows `DX12 device: WARP (software adapter, --warp)`. A hardware-adapter
  run is not part of this check (the Demo exe in Phase 5 covers it).
- **Previously:** new.

### Cooked shaders re-cook identically on Windows

- **What:** the committed DXIL + SPIR-V blobs were cooked on Linux with the pinned DXC version (1.9.2602.24, from a
  conda-forge build of the same release because the GitHub release asset host was unreachable from the build box).
  Confirm the official Windows binary produces the same shaders.
- **Where:** `python third_party/tools/restore-tools.py --only dxc` (verifies the zip's SHA256), then
  `python scripts/cook_shaders.py --dxc third_party/tools/dxc/1.9.2602.24/win-x64/bin/x64/dxc.exe`.
- **Expected:** `cooked 6 DXIL + 6 SPIR-V ... (failures: 0)`. The `.spv` files should be byte-identical to the
  committed ones (`git status` shows no change under `assets/shaders/cooked/*.spv`). The `.dxil` files may differ
  in their embedded hash only; if `git status` shows them modified, commit the Windows-cooked DXIL as the canonical
  blobs and note it in `docs/testing.md`.
- **Previously:** new.

### Lavapipe on Windows (optional)

- **What:** the per-process lavapipe wrapper runs the Vulkan tests without a GPU driver, and the run reports how
  many of them RAN.
- **Where:** needs the 7-Zip command line on PATH. `python third_party/tools/restore-tools.py --only mesa_lavapipe`
  then `python scripts/vk_lavapipe.py --check -- dotnet test tests/MewStage.Rendering.Tests`.
- **Expected:** `vk_lavapipe: --check: vulkaninfo devices: llvmpipe ...` (or the note that vulkaninfo is not on
  PATH), the test run passes, and the output contains `[lavapipe] 5 lavapipe tests RAN in MewStage.Rendering.Tests`.
  The wrapper refuses to run from an elevated shell by design.
- **Previously:** new.

## Phase 3: UI backend

### UI tests on Windows (headless + WARP)

- **What:** the MewUI backend's headless tests (host, allocation gates, fonts, external textures, input bridge,
  gamepad navigator) pass on Windows, where `Stopwatch` resolution, the default font rasterization and the
  FontStashSharp build can differ from Linux.
- **Where:** `dotnet test tests/MewStage.Ui.Tests`.
- **Expected:** `Passed! ... Passed: 58, Skipped: 14` (the 14 skips are the lavapipe-gated tests). The two
  allocation gates (`Host_OnRequest_Idle_AddsNoPass_AllocatesZero`, `Sink_SteadyState_AllocatesZero`) must report 0 B;
  they run with tiered compilation off, so a nonzero number is a real allocation, not JIT noise.
- **Previously:** new.

### Lavapipe UI tests on Windows (optional)

- **What:** the host contract and sink gates (7 + 7 tests) run on the software Vulkan driver and the run reports that
  they RAN.
- **Where:** `python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Ui.Tests`.
- **Expected:** `Passed! ... Passed: 72`, and the output contains `[lavapipe] 14 lavapipe tests RAN in
  MewStage.Ui.Tests`. The measured values printed by the gates (`[opacity] mid=(128, 128, 128)`,
  `[clip-edge] x40=(255, 255, 255) x39=(8, 8, 13)`, `[star] center == point`) should match the Linux run; a
  difference of more than 2 in a channel is worth a look, since both runs are the same llvmpipe code.
- **Previously:** new.

### Text looks right

- **What:** nothing in this phase draws to a screen yet, but the glyph path is now wired end to end (FontStashSharp
  atlas -> R8 texture upload -> the text pipeline). When the first windowed demo appears in Phase 5, read a line of
  Roboto at 14 and 24 px against a dark panel and check for the usual atlas faults: missing glyphs, glyphs from the
  wrong string, a half-pixel smear on the vertical stems, and a white box where a glyph should be.
- **Where:** deferred to the Phase 5 block (the Overlay demo); recorded here so the check is not forgotten.
- **Previously:** new.

## Phase 4: Host

### Host tests on Windows

- **What:** the SDL translation, gamepad bookkeeping, asset-root lookup, cooked-shader loader and the layering /
  hygiene gates pass on Windows (path separators and the development walk-up differ from Linux).
- **Where:** `dotnet test tests/MewStage.Host.Tests`.
- **Expected:** `Passed! ... Passed: 56, Skipped: 5` (the 5 skips are the lavapipe-gated offscreen host tests).
- **Previously:** new.

### Offscreen host stack on lavapipe (optional)

- **What:** the whole windowed stack runs offscreen on Windows too: SDL's offscreen video driver, the headless Vulkan
  device on lavapipe, the offscreen swapchain, the real frame loop, a secondary window.
- **Where:** `python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Host.Tests`.
- **Expected:** `Passed: 61` and `[lavapipe] 5 lavapipe tests RAN in MewStage.Host.Tests`; the printed values match
  Linux: `[smoke] corner=(40, 90, 180) far=(40, 90, 180) whiteText=150`, `[overlay] corner G=255.0`,
  `[secondary] mid=(200, 40, 40)`.
- **Previously:** new.

### A label in a window, DX12 and Vulkan

- **What:** the Phase 4 "done when": a real OS window with a MewUI label, on both backends. There is no demo exe yet
  (Phase 5), so this needs a 15-line throwaway console project that references `src/MewStage.Host` and runs:
  `using var host = new AppHost(GpuBackends.Select(args), "MewStage smoke", 960, 540, null, 0, 0);`
  `host.Ui.SetWindow(new Window { Padding = Thickness.Zero }.Content(new Border { Background = Color.FromArgb(255, 40, 90, 180) }.Child(new Label().Text("MewStage"))));`
  `host.Run();` (with `using Aprillz.MewUI; using Aprillz.MewUI.Controls; using MewStage.Host;`). Run it once with
  no flags (DX12) and once with `--render-vk`. If you would rather wait, the Phase 5 start page is the same check with
  a real UI; the item stays here until one of the two has been seen.
- **Expected:** a resizable window, blue edge to edge (the window's 8 DIP padding is zeroed), "MewStage" in Roboto
  near the top-left, no flicker; dragging the window edge resizes the panel with it; the close button ends the
  process cleanly (no exception on exit, no device-removed message). On a 125 % / 150 % monitor the text is the
  same physical size as a 14 px label in any other app (DIP layout), and moving the window to a monitor with another
  scale re-sizes it after one frame.
- **Previously:** new.

### Gamepad connect while running (optional, needs a pad)

- **What:** `SdlGamepadCapture` opens a pad plugged in after start-up and the navigator reads it.
- **Where:** the same throwaway project with `host.EventHook = (e, input) => capture.TryHandleEvent(e, input);`
  and `SdlGamepadCapture.InitFlags` passed as `extraInitFlags`; a window with two or three buttons.
- **Expected:** `[input] +gamepad '<name>' guid=<32 hex chars>` on the console when the pad is plugged in; the
  D-pad moves the focus ring between the buttons; unplugging prints nothing and the focus ring stays where it is.
- **Previously:** new.

## Phase 5: Overlay mode

### Demo tests on Windows

- **What:** the headless demo tests (command line, styles and templates, the HUD's bindings and transition, the pause
  menu's commands, text input and gamepad engage model) pass on Windows.
- **Where:** `dotnet test tests/MewStage.Demo.Tests`.
- **Expected:** `Passed! ... Passed: 21, Skipped: 8` (the 8 skips are the lavapipe-gated journeys).
- **Previously:** new.

### Overlay journeys on lavapipe (optional)

- **What:** the eight Overlay / start page journeys run offscreen on Windows through the real `DemoApp` and dump
  PNGs.
- **Where:** `set MEWSTAGE_JOURNEY_OUT=%TEMP%\mewstage-journeys` then
  `python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Demo.Tests`.
- **Expected:** `Passed: 29` and `[lavapipe] 8 lavapipe tests RAN in MewStage.Demo.Tests`; the printed values match
  Linux within a few units: `[overlay] center=(164, 135, 43) sky=(19, 22, 28)`, `[pause] world (19, 22, 28) ->
  (8, 9, 12); panel (30, 33, 41)`, `[dialog] faded in over N frames` with N between 2 and 20, `[combo]
  panel-colored pixels below the box: 67xx -> 30xx of 7560`, `[letterbox] ... lit=173`. The PNGs in the output
  folder (`overlay-hud`, `overlay-pause`, `overlay-dialog`, `overlay-combo`, `overlay-label`, `overlay-letterbox`,
  `start-page`) look like the Linux ones: a lit cube on a grid, the HUD, the pause panel with its list open, the
  dialog over the dimmed menu.
- **Previously:** new.

### The start page and Overlay mode, DX12 and Vulkan

- **What:** the Phase 5 "done when": the demo exe in a real window on both backends. This replaces the Phase 4
  throwaway project (the start page is that label test with a real UI).
- **Where:** `dotnet run --project src/MewStage.Demo` (DX12), `dotnet run --project src/MewStage.Demo -- --render-vk`
  (Vulkan), and once with `--overlay` to skip the start page. `--warp` runs DX12 on the software adapter.
- **Expected:**
  - Start page: a dark 1280 x 720 window, "MewStage" in Lora, three buttons (Overlay filled blue, Workbench grayed
    out with no handler yet, Kit outlined), a description beside each, the backend name in the status line. Hovering
    a button fades its fill; Tab moves a focus ring between the two enabled buttons; Enter on Overlay enters the mode.
    Kit relaunches the exe with `--standard`, which in this build prints "Kit mode (--standard) is not in this build
    yet." and exits 3 (the Kit phase fills it in); the start page window closes.
  - Overlay: the lit cube spinning over a floor grid with red / green axes, the camera orbiting slowly. Top-left
    "MewStage / Overlay" with the frame time and angle changing every frame; top-right the three prompts with their
    SVG icons; "Esc or Start: menu" at the bottom, fading out after 3 s; "1920 x 1080 design space" in the far
    bottom-right corner. Resize the window: the HUD scales uniformly and keeps its corners (letterboxed when the
    aspect is not 16:9), the world fills the whole window.
  - Escape (or Start on a pad): the world dims, the cube stops, the pause panel shows; the mouse hovers / clicks
    everything, the pad's D-pad moves the focus ring, A engages the slider (Left / Right adjust, B leaves it), the
    ComboBox list opens inside the window and A / Enter picks an item, Space toggles the floor grid (the grid
    disappears behind the panel at once), typing in the HUD label box shows the text on the HUD after Resume.
    Escape / B / Resume returns to the world spinning; "Start page" goes back to the picker.
  - Quit opens the confirmation dialog, fading in over about a quarter second; Escape / Cancel closes it, Quit ends
    the process cleanly (exit code 0, no device-removed message, no exception).
  - DX12 and Vulkan look the same (same colors, same text, the HUD right side up on both).
- **Previously:** new.

### HUD text at display scale (optional)

- **What:** the HUD is laid out in design space, so its text size follows the window, not the monitor scale.
- **Where:** Overlay mode on a 125 % or 150 % monitor, then drag the window to a 100 % monitor.
- **Expected:** the HUD looks identical on both monitors at the same window size (unlike the start page, whose text
  follows the display scale like any desktop app); the pointer hits buttons where they are drawn on both.
- **Previously:** new.

## Phase 6: Viewport and Workbench

### Demo tests on Windows (again)

- **What:** the headless demo tests now include the viewport bridge (compositor bookkeeping, projection, labels) and
  the Workbench's pieces (layout JSON, view model, inspector, outliner).
- **Where:** `dotnet test tests/MewStage.Demo.Tests`.
- **Expected:** `Passed! ... Passed: 29, Skipped: 15` (the 15 skips are the lavapipe-gated journeys).
- **Previously:** new.

### Workbench journeys on lavapipe (optional)

- **What:** the seven Workbench journeys run offscreen on Windows: viewport as an external image, two viewports,
  resize re-allocation, dock layout save / restore across runs, the stats window, viewport gestures, inspector + About.
- **Where:** `set MEWSTAGE_JOURNEY_OUT=%TEMP%\mewstage-journeys` then
  `python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Demo.Tests`. The journeys write their layout file under
  `%TEMP%\mewstage-tests\...`, never under `%APPDATA%\MewStage`.
- **Expected:** `Passed: 44` and `[lavapipe] 15 lavapipe tests RAN in MewStage.Demo.Tests`; the printed values match
  Linux within a few units: `[workbench] viewport corner=(28, 31, 38) center=(111, 122, 51)` (the center varies with
  the cube's angle; it is bright), `[two] ... B corner (41, 38, 48)`, `[resize] target 721x421 gen N -> 575x364 gen
  N+1`, `[stats-window] 560x320`, `[about] center pixel (36, 39, 48)`. The PNGs `workbench`, `workbench-two`,
  `workbench-stats-window`, `workbench-about` look like the Linux ones: a docked tool window with the cube in the
  viewport, the outliner with icons, the inspector, the frame-time chart.
- **Previously:** new.

### Vulkan validation layer (optional)

- **What:** every lavapipe journey runs clean under `VK_LAYER_KHRONOS_validation` (no VUID message). The build box
  found and fixed two: UI pipelines did not name the stencil format of the D24S8 attachment they draw into, and an
  offscreen swapchain's backbuffer was transitioned to the swapchain-only PRESENT_SRC layout.
- **Where:** install the Vulkan SDK (it carries the layer), then
  `set MEWSTAGE_VK_VALIDATION=1` and `python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Demo.Tests --logger "console;verbosity=detailed"`.
- **Expected:** the console shows `[vk] validation layer + debug messenger ENABLED` once per device and no line
  starting with `[VK-VALIDATION`. With a real window on Vulkan (`dotnet run --project src/MewStage.Demo -- --render-vk
  --workbench` with the variable set) the same: nothing on stderr while the workbench runs, resizes and quits.
- **Previously:** new.

### Workbench mode, DX12 and Vulkan

- **What:** the Phase 6 "done when": a 3D viewport inside a docked tool UI, in a real window on both backends.
- **Where:** `dotnet run --project src/MewStage.Demo -- --workbench` (DX12) and `-- --workbench --render-vk`; also
  from the start page's Workbench button.
- **Expected:**
  - A 1280 x 720 window in DIP space (the text is the size of any desktop app's; on a 125 % monitor everything is
    25 % larger, including the viewport's pixels): menu bar (File / View / Help), a toolbar of SVG icon buttons, the
    dock with Outliner (left), Viewport (center, a "Cube" label floating above the cube), Stats (below it, a line
    chart scrolling left and a draw-call bar), Inspector (right), and a status bar (backend, frame time, viewport
    sizes). Hovering does not move the keyboard focus (a text box keeps it); Tab walks the controls.
  - Viewport: left-drag orbits, the wheel zooms, middle-drag (or Shift + left-drag) pans, F frames the cube. Dragging
    the splitter between Viewport and Inspector resizes the viewport: the image stretches for one frame, then
    redraws at the new size (the status bar's "Viewport W x H" follows).
  - View > Viewport 2 (Ctrl+2) adds a second tab in the viewport group with a top-down camera; drag the tab beside the
    first (or right-click it for Split) to see both: the same cube at the same angle from two cameras. Outliner:
    Camera A / Camera B show each camera's readout and a Frame cube button.
  - Inspector edits show in both viewports the next frame: the spin slider and its text box agree (type 2.5 and
    Enter), Color, Scale, Edges only, Floor grid; Light > Yaw / Pitch move the shading.
  - View > Stats Window (Ctrl+Shift+S) moves the chart into a second OS window (no taskbar entry, owned by the main
    one); its range ComboBox opens inside that window; closing it (or the menu item again) docks the chart back.
  - Help > About opens a dialog in the window's overlay layer; Escape or Close dismisses it.
  - Close the window, start again: the dock layout (Viewport 2 on, moved panes, pane sizes) is restored from
    `%APPDATA%\MewStage\workbench-layout.json`; View > Reset Layout puts it back.
  - File > Quit (Ctrl+Q) exits cleanly on both backends; DX12 and Vulkan look the same.
- **Previously:** new.

## Phase 7: Kit mode

### Demo tests on Windows (Debug and Release)

- **What:** the headless demo tests now include the kit page (seven tabs built and rendered on the embedded host),
  the geometry sample's Boolean areas and the DevTools gate, which has one branch per configuration.
- **Where:** `dotnet test tests/MewStage.Demo.Tests` and `dotnet test tests/MewStage.Demo.Tests -c Release`.
- **Expected:** Debug `Passed: 32, Skipped: 16`; Release the same counts. In Debug the gate test proves
  `WindowDevTools.IsSupported`, toggles the inspector and the performance monitor on a headless window and finds the
  two window-opening gestures already unmapped; in Release it proves the DevTools are absent.
- **Previously:** new.

### DevTools keys on the embedded host (lavapipe, optional)

- **What:** the new Workbench journey presses Ctrl+Shift+P (the performance monitor draws top-right through our
  backend), Ctrl+Shift+T (nothing: unmapped on the embedded host) and Ctrl+Shift+S (the Stats Window, moved off
  Ctrl+Shift+T so it never collides with MewUI's visual tree).
- **Where:** `set MEWSTAGE_JOURNEY_OUT=%TEMP%\mewstage-journeys` then
  `python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Demo.Tests`.
- **Expected:** `Passed: 48` and `[lavapipe] 16 lavapipe tests RAN in MewStage.Demo.Tests`; the console shows
  `[devtools] bright px top-right: 6xx -> 47xx with the performance monitor on` (the second number is the overlay's
  text and border); `workbench-devtools-perf.png` shows MewUI's two yellow-bordered panels over the inspector.
- **Previously:** new.

### Kit mode on Windows (Win32 platform host + MewVG GL)

- **What:** the Phase 7 "done when": the same demo controls, styles and view models on MewUI's own window host, with
  all four DevTools working.
- **Where:** `dotnet run --project src/MewStage.Demo -- --standard`, and from the start page's Kit button (the
  demo restarts itself with the flag).
- **Expected:**
  - A 1280 x 800 "MewStage Kit" window with seven tabs. Buttons: the Primary and Ghost demo buttons (hover lightens,
    press darkens, Tab draws the focus ring, the two disabled ones are gray) above a row of upstream's stock controls
    (Button, CheckBox, two RadioButtons, ToggleSwitch, Slider, ProgressBar, NumericUpDown, TextBox, ComboBox, an
    Expander in a GroupBox). Inspector: the Workbench outliner and property grid over a stand-alone view model
    (click Light to see the yaw/pitch sliders). HUD: the 1920 x 1080 HUD panel at 1:1 on its dark background inside
    a scroller (scroll right and down to the corner marker). Icons: the eight SVG icons at 24, 36 and 64. Chart: a
    smoothed line and a column chart over synthetic data. Geometry: Union, Intersect, Xor and Exclude of a square
    and a circle. Type: Roboto at 14/20/32 and Lora Regular, Italic, Bold and Bold Italic at 20 and 32.
  - The status bar reads `DevTools: Ctrl+Shift+I inspector | Ctrl+Shift+T visual tree | ...` (a Debug build).
  - Ctrl+Shift+I: the inspector overlay follows the mouse and names the element under it. Ctrl+Shift+P: the
    performance monitor's two panels top-right with live numbers. Ctrl+Shift+T: a "Live Visual Tree" window opens
    (a second OS window) with the element tree and a property panel; clicking a node highlights it in the kit
    window. Ctrl+Shift+Alt+P: a "MewUI Profiler" window with the frame timeline. Each shortcut again closes its tool.
  - Close the window: the process exits with code 0.
- **Previously:** new. On the build box the same was verified on the X11 host under Xvfb with
  `scripts/kit-smoke-x11.sh` (every tab, all four tools, the tool windows counted by title).

### Hot Reload (Debug JIT)

- **What:** editing the kit page's composition under `dotnet watch` re-composes the window without a restart and
  keeps the view model state.
- **Where:** `dotnet watch run --project src/MewStage.Demo -- --standard`. Once the window is up, change the spin
  speed in the Inspector tab (so there is state to keep), then edit `KitPage.Compose` in
  `src/MewStage.Demo/Kit/KitPage.cs`: for instance the status bar's `.FontSize(12)` to `.FontSize(22)`, or the
  status Border's color. Save.
- **Expected:** `dotnet watch` prints `Hot reload ... changes applied in N ms`; within a second the status bar's text
  grows, the tabs stay, and the Inspector tab still shows the spin speed you set. Edit a string inside
  `KitSamples.Buttons` instead: the change is applied to the process but the window does not re-compose (a helper in
  another type; see docs/devtools.md). Revert the edits afterwards.
- **Previously:** new. Verified on the build box under Xvfb: `Hot reload ... changes applied in 733ms`, the status
  bar re-rendered at 22 px.

### Editor Preview (optional, VS Code)

- **What:** the MewUI editor preview shows the Kit window in an editor panel without a window on screen.
- **Where:** install the MewUI VS Code extension, open `src/MewStage.Demo/Kit/KitPage.cs`, run "MewUI: Start
  Preview" from the command palette.
- **Expected:** the target dropdown lists "Application main window" (the Kit window, because `Design.IsPreviewMode`
  routes `Main` to Kit mode under the preview) and the demo's `Window` subclasses; no SDL window opens. Saving an
  edit in `KitPage.Compose` updates the panel.
- **Previously:** new; not verifiable on the build box (no VS Code).

### Overlay and Workbench with the DevTools on (Debug)

- **What:** in a Debug build the two in-window DevTools now work on the embedded host, and the two that need a
  platform host are inert instead of throwing.
- **Where:** `dotnet run --project src/MewStage.Demo -- --workbench`, then the keys.
- **Expected:** Ctrl+Shift+I shows the inspector overlay composited into the Workbench (it names the dock panes and
  the viewport control under the mouse); Ctrl+Shift+P shows the performance monitor top-right with `Loop (not
  running)` on its last line (MewUI's loop is replaced by the app's frame). Ctrl+Shift+T and Ctrl+Shift+Alt+P do
  nothing. View > Stats Window is now Ctrl+Shift+S. The same in Overlay mode over the HUD.
- **Previously:** Phase 6 listed Ctrl+Shift+T for the Stats Window; changed in this phase.

## Phase 8: AOT, docs, CI

### NativeAOT publish on Windows

- **What:** the Phase 8 "done when": the published Windows exe runs the Overlay and the Workbench on DX12 and on
  Vulkan, and Kit mode on the Win32 platform host, from a self-contained folder.
- **Where:** `dotnet publish src/MewStage.Demo -c Release -r win-x64 -p:PublishAot=true -o publish/win-x64` (needs
  the Visual Studio C++ build tools the ILCompiler links with), then from a different folder, so the exe cannot find
  the source tree's assets: `..\publish\win-x64\MewStage.Demo.exe --overlay`, `--overlay --render-vk`,
  `--workbench`, `--workbench --render-vk`, `--standard`.
- **Expected:** the publish ends with `MewStage: copied 32 asset files to ...publish\win-x64\assets` and the ILC
  warnings IL3000 / IL3002 under Silk.NET only (visible, not errors). The folder holds `MewStage.Demo.exe`,
  `SDL3.dll`, `MewStage.Demo.xml`, `assets\` and the pdb. Every mode starts and looks like the JIT build; the Kit's
  status bar reads `DevTools are not in this build`. Report the exe size (the Linux one is 59 MB with symbols, 21 MB
  stripped; the whole Linux folder is 65 MB).
- **Previously:** new. The linux-x64 publish was verified on the build box: all three modes start on an Xvfb screen
  (SDL X11 window + lavapipe swapchain; Kit on X11 + Mesa GL), from a folder away from the source tree.

### CI on GitHub

- **What:** `.github/workflows/ci.yml` runs on the first push: Linux (build, format gate, headless tests, lavapipe
  with the validation layer, the Xvfb kit smoke, screenshots as an artifact), Windows (build, headless tests incl.
  the DX12 WARP smoke, the win-x64 AOT publish as an artifact), the linux-x64 AOT publish started in all three
  modes, and `vendor.py --check`.
- **Where:** push to `main` (or open a pull request) and watch the Actions tab.
- **Expected:** four green jobs. The Linux job's log shows four `[lavapipe] N lavapipe tests RAN` lines (5, 17, 5,
  16) and `[kit-smoke] OK`; the Windows job's test summary shows the DX12 smoke passing rather than skipped; the
  `vendor-lock` job prints each source as verified. The one thing not run on the build box is the Windows job (no
  Windows there). `vendor.py --check` ran there with `--tarball-dir` (the container's proxy refuses codeload, so the
  tarballs came from another machine), so a `vendor-lock` failure is a CI-environment problem to read, not a code
  change to expect.
- **Previously:** new.

### Documentation read-through

- **What:** the README and `docs/` read as intended for someone who has never seen MewUI: the WHY, the quick start,
  the "how to create your own app" sample compiles against the libraries as written.
- **Where:** `README.md`, `docs/architecture.md`, `docs/mewui-embedding.md`, `docs/vendoring.md`,
  `docs/testing.md`, `docs/devtools.md`. For the sample: a folder beside the MewStage checkout holding the README's
  project file and program, as written.
- **Expected:** the sample opens a window with the centered text on DX12 (the build box ran it on Vulkan under
  Xvfb); the screenshots in `docs/images` match the running demo. Spelling is American throughout (a sweep converted the build's British spellings in comments, strings
  and a few identifiers, such as `Centre` -> `Center` on the scene).
- **Previously:** new.

## Scratch surfaces and window close

MewUI's caches (an SVG `Image` in its default Cached mode, `BitmapCache`) now render on the embedded host, into
scratch surfaces recorded inside the `ui` pass, and `Window.Close()` now closes an embedded window. Before, the demo's
icons drew their paths on every repaint (`ImageVectorCacheMode.Direct`, the cached path threw), and closing an
embedded window did nothing.

### UI tests on Windows (again)

- **What:** the headless UI suite now includes the scratch-surface tests and the window-close tests.
- **Where:** `dotnet test tests/MewStage.Ui.Tests`.
- **Expected:** `Passed! ... Passed: 67, Skipped: 17` (the 17 skips are the lavapipe-gated tests). With
  `python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Ui.Tests`: `Passed: 84` and
  `[lavapipe] 17 lavapipe tests RAN in MewStage.Ui.Tests`.
- **Previously:** 58 pass / 14 skip headless, 72 on lavapipe.

### Cached icons look the same, DX12 and Vulkan

- **What:** the Workbench toolbar and Outliner icons and the Overlay HUD legend icons are rasterized once into caches
  and blitted after that.
- **Where:** `dotnet run --project src/MewStage.Demo -- --workbench` and `-- --overlay`, each with and without
  `--render-vk`.
- **Expected:**
  - The icons look as they did: crisp 1.8 px strokes, no blur, no dark fringe around the strokes (a fringe would mean
    the cache is blended as straight alpha instead of premultiplied).
  - Workbench status bar ends with `UI caches 10, rasterized 10x`. The second number stays put while you hover the
    toolbar, open menus, orbit the viewport and resize the window.
  - Drag the window to a monitor with a different scale (100 % to 125 %): the second number goes up once per icon
    (new pixel size), then stays put again; the icons are sharp at the new scale.
  - The HUD legend's three icons (play, close, pause) next to `A / Enter`, `B / Esc`, `Start / Esc` look as before at
    any window size, including a letterboxed 1024 x 600.
- **Previously:** the icons drew their paths every repaint (Direct mode). There was no cache readout.

### Leaving a screen closes its windows

- **What:** the start page, Overlay and Workbench close their windows when the demo switches modes, and Overlay closes
  the pause menu and quit dialog when they are dismissed. A closed window's icon caches go back to a pool that the
  next screen reuses.
- **Where:** the Workbench (`-- --workbench`): Escape to the start page and back several times; the Overlay (`--
  --overlay`): Escape, Quit, Cancel, Resume a few times, then the pause menu's Start page button.
- **Expected:**
  - Workbench status bar after each re-entry: `UI caches 10`, and the rasterized count goes up by 10 per entry (each
    entry is a new window with new icons that rasterize into pooled surfaces) instead of the cache count growing.
  - Every switch and every dismissed menu is clean: no exception on the console, no flicker of the closed window, the
    window under it takes input at once (Escape on the HUD opens the menu again).
  - With `set MEWSTAGE_VK_VALIDATION=1` and `--render-vk`: no `[VK-VALIDATION` line while switching and on quit.
- **Previously:** the demo dropped its windows without closing them; their icon caches stayed allocated until the app
  quit, and `Window.Close()` on an embedded window was a no-op.

## MewUI re-pin (2026-11-09)

MewUI moved from `8b9c9433` to `8c43abf3` (140 upstream commits). The text/0003 font-registry patch was dropped
because upstream now registers every face of a family itself, and the embedded host gained a dispatcher (vendor
patch `multi-host/0003`) and upstream's pulse. Before, every `DispatcherTimer` and posted job in Overlay and
Workbench silently did nothing.

### UI tests on Windows (again)

- **What:** the headless UI suite now includes the dispatcher, pulse, pseudo-handle and vertical-slider tests.
- **Where:** `dotnet test tests/MewStage.Ui.Tests`.
- **Expected:** `Passed! ... Passed: 74, Skipped: 17`. With
  `python scripts/vk_lavapipe.py -- dotnet test tests/MewStage.Ui.Tests`: `Passed: 91` and
  `[lavapipe] 17 lavapipe tests RAN in MewStage.Ui.Tests`.
- **Previously:** 67 pass / 17 skip headless, 84 on lavapipe.

### The caret blinks in the embedded modes

- **What:** a focused TextBox blinks its caret in Overlay and Workbench.
- **Where:** `dotnet run --project src/MewStage.Demo -- --overlay`, Escape, click the pause menu's text box; then
  `-- --workbench`, click the Inspector's numeric text box. Both with and without `--render-vk`.
- **Expected:**
  - The caret blinks about twice a second and stays solid while you type.
  - Holding a key in the text box repeats it as before; Escape still closes the menu one window at a time.
- **Previously:** the caret was drawn solid and never blinked (its timer never ticked without a dispatcher).

### Kit mode still draws every Lora face (Win32)

- **What:** the Kit's Type tab draws Lora Regular, Italic, Bold and Bold Italic as four different faces. This now
  comes from upstream's own multi-face font registry instead of MewStage's dropped text/0003 patch.
- **Where:** `dotnet run --project src/MewStage.Demo -- --standard`, the Type tab.
- **Expected:** the four Lora lines differ visibly (slant and weight) at 20 and 32; Roboto 14 / 20 / 32 above them
  look as before. The X11 kit smoke screenshot of the Type tab (`kit-tab6.png` under `MEWSTAGE_JOURNEY_OUT`) shows
  the same on Linux; the build box checked it.
- **Previously:** the same picture, through the text/0003 patch.

### Workbench menus

- **What:** the menu bar's three menus open (the demo now builds them with `SubMenu`, upstream's new name for the
  old `Menu` setter).
- **Where:** `-- --workbench`, click File, View and Help, and use their items.
- **Expected:** each menu opens under its header; View's two check items follow Viewport 2 and the stats window;
  About opens the About panel.
- **Previously:** the same, built with the now-obsolete `MenuItem.Menu(...)`.

