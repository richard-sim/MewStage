# Architecture

MewStage is nine projects in a strict layering, one executable at the top. The layering is a test
(`MewStage.Host.Tests/Architecture/LayeringTests`): it reads every project's `ProjectReference` closure and fails when
a project reaches one it may not. This page says what each project is for, how a frame runs, and how the NativeAOT
publish is set up.

## The layering

```
MewStage.Demo ........ the executable: start page, Overlay, Workbench, Kit
  |-- MewStage.Scene ........ cube mesh, orbit camera, cube + grid passes         (Graphics, Rendering)
  |-- MewStage.Viewport ..... offscreen target -> MewUI control                   (Ui.Backend, Rendering, Graphics, MewUI)
  '-- MewStage.Host ......... SDL3 window, device, frame loop, secondary windows  (Ui.Backend, Rendering, Graphics, Input)
        '-- MewStage.Ui.Backend ..... the embedded MewUI host                      (Rendering, Graphics, Input, Ui.Navigation, MewUI, MewVG)
              |-- MewStage.Ui.Navigation ... NavCommands, gamepad focus rules      (MewUI)
              |-- MewStage.Input .......... the input snapshot vocabulary          (nothing)
              '-- MewStage.Rendering ...... backends, render graph, scheduler      (Graphics)
                    '-- MewStage.Graphics ... the GPU device seam                  (nothing)
```

| Project | May reference | What it is |
| --- | --- | --- |
| `MewStage.Graphics` | nothing | The GPU device seam: `IGraphicsDevice`, `IGraphicsRecorder`, resource and pipeline descriptions, enums, capabilities, a no-op profiler hook. No backend code and no packages. |
| `MewStage.Input` | nothing | `RawInputSnapshot`, `DeviceSnapshot`, `KeyCode`, `MouseButton`, gamepad enums. The input vocabulary with no SDL in it, so the UI backend can be tested without a window. |
| `MewStage.Rendering` | Graphics | The backends (Vulkan through Silk.NET, DX12 through Vortice, a headless device for tests, a Metal stub), `OffscreenSwapchain`, the `RenderGraph` and its compiler, the `GpuScheduler` with frames in flight and device-loss handling, `RenderCamera`. |
| `MewStage.Ui.Navigation` | MewUI | `NavCommands` (Back, Confirm, tabs, actions as MewUI `Command`s), gamepad focus rules (`GamepadClaim`, `HoverPark`, `FocusFallback`) and the focus interfaces controls implement. Pure MewUI; no GPU, no SDL. |
| `MewStage.Ui.Backend` | Graphics, Rendering, Input, Ui.Navigation, MewUI, MewVG.Core | The embedded MewUI host: the fake window backend, the graphics factory over `IGraphicsDevice`, the render surface, the MewVG bridge partial, the NanoVG-class renderer over the app's recorder, fonts through FontStashSharp, the external-texture registry, the SDL input bridge, the gamepad navigator. The only project that compiles against MewUI and MewVG internals (docs/mewui-embedding.md). |
| `MewStage.Scene` | Graphics, Rendering | The 3D content: a cube mesh, an orbit camera, scene settings, and `CubeSceneRenderer`, which adds a grid pass and a cube pass to any render graph target. It knows nothing about UI. |
| `MewStage.Viewport` | Graphics, Rendering, Ui.Backend, MewUI | The bridge from an offscreen render target to a MewUI control: `ViewportTarget`, `ViewportCompositor`, `ViewportControl`, `TextureReadback`. It knows nothing about SDL or the cube. |
| `MewStage.Host` | Graphics, Rendering, Input, Ui.Navigation, Ui.Backend, MewUI | The application shell: `GpuWindow` (SDL3 window + device + swapchain + scheduler + graph), `UiLayer`, `AppHost` (the frame loop), `SecondaryWindow`, the SDL key and gamepad maps, `AssetPaths`, `UserSettings`. The only project that references SDL. |
| `MewStage.Demo` | everything above, plus the vendored extensions and platform hosts | The executable: the start page and the three modes (README, quick start). All of its types are internal. |

Two rules the diagram does not show: `Scene` and `Viewport` never meet (the Demo's `CubeViewportRenderer` joins them),
and only `Ui.Backend` uses `InternalsVisibleTo` grants into the vendored trees. Every other project uses MewUI's public
API, as any application would.

## A frame

`AppHost.RunFrame` is the frame law; everything the demo does hangs off its hooks.

1. Pump SDL events into a `RawInputSnapshot` (`ApplyEvent`); the application's `EventHook` sees each event first
   (the gamepad capture takes its own). `AfterPump` runs (the demo switches modes here, with the device idle).
2. `UiLayer.Dispatch(input, dt)`: the input bridge turns the snapshot into MewUI mouse, key, wheel and text events for
   the top window of the stack; the gamepad navigator moves focus and runs `NavCommands`.
3. Resize if the window size or DPI changed: the swapchain, then the UI surface (`EmbeddedUiHost.Resize`).
4. `GpuWindow.BeginFrame`: acquire the backbuffer, start the scheduler frame (one upload ring and one descriptor
   budget per frame in flight), reset the render graph, import the backbuffer in state `Common`.
5. `BeforeUi(graph, backbuffer)`: the application adds world passes. Overlay draws the cube straight into the
   backbuffer; the Workbench's `ViewportCompositor` draws each viewport into its own offscreen target and declares
   the targets to the UI host as sampled textures.
6. The `ui` pass: `EmbeddedUiHost.TickAndRender`. It pulses the animation clocks once, lays out every visible window
   that is dirty, and adds one graph pass that writes the backbuffer (CLEAR to `Background` when `UiClearsBackground`,
   LOAD otherwise), reads every declared sampled texture, creates one transient D24S8 stencil, and renders the window
   stack bottom to top into one MewVG frame with one flush.
7. `AfterUi(graph)`: the Workbench's terminal passes return every viewport target to `Common`, the state the next
   frame's import declares.
8. `BeforePresent` (the tests hook a readback here), the present pass, `Submit` (the compiler orders the passes,
   emits the barriers per physical slot and aliases transients), secondary windows render on the same scheduler,
   `EndFrame`.

A frame whose UI did not change in OnRequest mode adds no `ui` pass and allocates nothing; the allocation gates in the
test suites hold that at 0 B.

## The render graph

A graph is rebuilt every frame: passes declare what they read and write and in which state, transients declare their
description, and imports bring in textures the application owns (the backbuffer, a viewport target) with the state
they are in. The compiler orders the passes, inserts the barriers, culls passes nothing reads, and aliases transient
memory. Executing a pass hands it an `IGraphicsRecorder` with its attachments already in the right state. The UI
backend is one such pass and never touches barriers itself, which is what lets the same backend draw over a world
(LOAD) or on a cleared target (CLEAR) and sample a viewport texture without knowing who produced it.

## NativeAOT

Every library sets `IsAotCompatible` (Directory.Build.targets), so the trim and AOT analyzers run on every build and a
warning is an error. The executable leaves `PublishAot` unset: `dotnet build` and `dotnet run` give a JIT app whose
runtime configuration does not say `IsDynamicCodeSupported = false`, which is what keeps MewUI's DevTools and Hot
Reload working in development (docs/devtools.md). Publishing turns AOT on from the command line:

    dotnet publish src/MewStage.Demo -c Release -r win-x64 -p:PublishAot=true -o publish/win-x64
    dotnet publish src/MewStage.Demo -c Release -r linux-x64 -p:PublishAot=true -o publish/linux-x64

The publish folder is self-contained: the native executable, SDL3's native library, and `assets/` (fonts, icons, the
cooked shaders), which `AssetPaths` treats as the packaged asset root. On the Linux build box the linux-x64 publish is
65 MB in all; the executable is 59 MB with its debug information and 21 MB after `strip`. The three settings behind it
sit in the Demo project under a `PublishAot == true` condition:

- `TrimMode = full`, `OptimizationPreference = Speed`, symbols kept.
- MewCharts is referenced with `GlobalPropertiesToRemove="PublishAot"`. Upstream's MewCharts project compiles
  LiveCharts2's engine from source and drops LiveCharts2's source generator when `PublishAot` is true, which leaves
  the generated `MotionProperty` members without an implementation and the build fails. Keeping the publish property
  out of that one reference lets the library build as it does for a JIT run; the ILCompiler then compiles it like
  every other assembly, and the Workbench's stats chart runs in the published binary.
- Two NuGet packages are not trim-annotated and the ILCompiler says so: `SharpGen.Runtime` (under Vortice) and
  `Silk.NET.Core`. Their per-assembly roll-ups (IL2104, IL3053) are silenced; the individual findings under Silk.NET
  (IL3000, IL3002: its path resolver reads `Assembly.Location` and the deps file, which a single-file app does not
  have) stay visible as warnings. They are harmless here because the Vulkan loader is found through the OS and
  `AppContext.BaseDirectory`. Every other IL code is still an error.

The DevTools are not in a published binary (upstream refuses to trim their reflection-based property panel and turns
them off), the Kit page's status bar says so, and Preview is JIT-only by design. Everything else runs: the linux-x64
publish starts Overlay and Workbench on an X11 SDL window with a lavapipe swapchain and the Kit on X11 + Mesa GL
(CI does this on every push); the win-x64 publish is a hand test (docs/hand-test.md, Phase 8).
