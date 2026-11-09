# Embedding MewUI

MewUI opens its own OS windows and draws with its own backends. MewStage does neither: the application owns the
window, the GPU device and the frame, and MewUI runs as one pass inside it. This page describes the two techniques
the demo shows, the contract the embedded host implements for MewUI, and the rules the renderer keeps. The code is
`src/MewStage.Ui.Backend` (the host) and `src/MewStage.Viewport` (the viewport bridge).

## What MewUI needs from a host

MewUI's internals are built around a few contracts that its platform hosts and backends implement. An embedded host
implements the same ones over the application's own resources.

| MewUI contract | MewStage's implementation |
| --- | --- |
| `IWindowBackend` | `EmbeddedWindowBackend`: a non-OS backend with a non-zero handle (so layout runs), a client size, an invalidation counter, and `Close()`, which the host turns into a real close (below). Everything else is a no-op. |
| `IGraphicsFactory` | `UiGraphicsFactory` over `IGraphicsDevice`: fonts, brushes, image decode, surfaces, contexts, readback. |
| `IRenderSurface` | `UiRenderSurface`: the target texture plus a D24S8 stencil, the MewVG context, the glyph atlases. |
| an image-source `IRenderSurface` (MewUI's caches) | `UiScratchSurface`: a color texture plus a D24S8 stencil, rendered inside the `ui` pass and sampled zero-copy ("Scratch surfaces" below). |
| the MewVG `IGraphicsContext` bridge | upstream's shared bridge source compiled under a MewStage branch (vendor patch `core-bridge`), plus `MewStageMewVGGraphicsContext.Platform.cs` for the frame bracket, text and images. |
| `MewVGContext` and the renderer behind it | `UiMewVGContext` and `NanoVgRenderer`, which records into the app's `IGraphicsRecorder` through one shader, `assets/shaders/ui.hlsl`. |
| the text contract | `FontStashText`: FontStashSharp rasterizes glyphs with the face's real advances and metrics, so measurement and drawing agree and measuring never needs a GPU surface. |
| the per-frame pulse | the host calls `AnimationManager.UpdateAt(timestamp)` once per frame; MewUI no longer ticks animations inside render. |

MewUI has no public API for a third-party backend. `MewStage.Ui.Backend` compiles against MewUI and MewVG internals
through `InternalsVisibleTo` grants kept in preserved files inside the vendored trees, the way upstream's own
backends do (docs/vendoring.md). No other MewStage project needs that: Viewport, Host and Demo use the public API.

## The host, per frame

`EmbeddedUiHost` keeps a stack of visible MewUI windows, bottom to top, and composites them into one render-graph
pass.

- `SetWindows(bottomToTop)` attaches a backend to each new window, applies the layout metrics (client size in pixels,
  DPI from the render scale, client size in DIPs), raises `Loaded` once the window is attached and sized, and prunes
  the backends of windows that left the stack. Only the top window is active: it alone gets input, focus visuals and
  the gamepad navigator, which is the modal trap a pause menu over a HUD relies on. In a Debug build the host also
  unmaps the two DevTools gestures that would open OS windows (docs/devtools.md).
- Pruning is not closing. A window that leaves the stack keeps its tree and can be shown again (a pause menu popped
  and pushed back). `Window.Close()` (or `CloseWindow(window)`) does what an OS backend's destroy does: `Closing` is
  asked and a handler can cancel it, `Closed` is raised, the window leaves the stack and the one under it becomes
  active, its graphics are released and its visual tree is disposed, so every element drops what it held (an
  `Image` hands its cached SVG back to the scratch pool, below). It works on a window in the stack and on one already
  pruned from it, and a closed window cannot be shown again (`SetWindows` throws). The close is synchronous, so a
  window should not close itself from inside its own input handler: the demo's Overlay mode queues a popped pause
  menu or dialog and closes it at the start of the next frame, before input is dispatched. A screen that goes away
  should close its windows; a dropped `Window` that is never closed keeps its caches leased until the host is
  disposed.
- `TrimCaches()` releases what the caches hold that no shown window leases: the scratch pool that closed windows
  returned their surfaces to, and idle cache entries. The pool keeps surfaces across screens on purpose (a re-entered
  screen reuses them), so trimming is for a transition where memory matters more, such as leaving an editor screen
  for the game. Call it between frames.
- `Resize(px, py, dpiScale)` recreates the surface and re-applies the metrics to every window. In design-space mode
  the render scale is `min(px / designW, py / designH)` and every window is laid out at the design size; otherwise the
  scale is the display scale and the DIP size is pixels over it.
- `TickAndRender(graph, target, mode, clearBackground)` does the frame: bind the thread's graphics factory, refresh
  command states (what MewUI's dispatcher would do after a drain), pulse the animation clocks, decide whether to render
  (`Continuous`, an invalidated window, an animation with render demand, or the first frame; otherwise add nothing),
  lay out every dirty window, and add the one `ui` pass. The pass writes the target (CLEAR or LOAD), reads every
  texture declared through `AddSampledTexture`, creates a transient D24S8 stencil cleared once, and in its execute
  renders each window into the surface's kept context, uploads glyph-atlas changes before the render pass opens, and
  flushes the whole stack as one MewVG frame.

Two details carry weight. A window keeps its graphics context across frames; one made per frame would re-realize every
text run every frame. And in `Continuous` mode the host stamps the retained renderer's pass clock at most 25 ms apart,
so a wall-clock stall never makes MewUI decide that a per-frame-changing visual (a viewport image) should be recorded
rather than drawn live.

The embedded defaults every host sets: popups, dialogs and drag previews open in the window's own layers instead of
OS windows (there are none), and the theme is pinned when no `Application` runs, because asking the OS theme throws.

### Scratch surfaces: SVGs and bitmap caches

MewUI rasterizes some things into an offscreen bitmap and draws the bitmap afterwards: an `Image` with a vector source
(an SVG) in its default `ImageVectorCacheMode.Cached`, an element with `CacheMode = BitmapCache`, a few filter
intermediates. It asks the graphics factory for a *scratch surface* (`AcquireScratchSurface`, which lands in
`IRenderDevice.CreateSurface` with an image-source descriptor), renders into it with `CreateContext(surface)` from
the control's `OnRender`, and shows it through `CreateImageView(surface)`.

On the embedded host that render has nowhere to go on its own: GPU work exists only inside the render graph's `ui`
pass. The host uses the fact that the windows' traversal runs inside that pass's execute but *before* the pass opens
its `BeginRendering` (the traversal is CPU-only, MewVG records draws, the host flushes them once at the end). A scratch
render is recorded right there, into the same command list, ahead of the window's render pass:

1. `UiRenderDevice.CreateSurface` returns a `UiScratchSurface` for an image-source descriptor: a B8G8R8A8 color
   texture (render target + shader resource) and a D24S8 stencil, allocated on first use, with their own resource
   states, since they are the backend's textures and not the graph's.
2. `CreateContext(surface)` returns a context attached to the device's one *scratch* MewVG context and text renderer.
   `BeginFrame` requires an open frame (`UiRenderDevice.BeginFrameRecording`, which `UiExecute` wraps the traversal in;
   outside it the call throws, which is how a misuse shows up as an exception instead of a hang).
3. `EndFrame` uploads any pending glyph-atlas changes, transitions the textures (color to RenderTarget, stencil to
   DepthWrite), opens its own `BeginRendering` cleared to transparent, flushes the scratch MewVG frame into it, ends
   the rendering and transitions the color texture to ShaderResource, the state the `ui` pass samples it in a moment
   later.
4. `CreateImageView(surface)` is the viewport's zero-copy path: a `GpuTextureSource` over the color texture's SRV
   registered in `ExternalTextures`, so the window draws the cache as any other image. Nothing is copied back.

The scratch MewVG renderer runs in *transient-buffer* mode: a fresh vertex / uniform buffer per flush, retired through
the device's deferred destroy, because several caches can rasterize in one frame and a per-frame ring buffer would have
the second overwrite the first before the GPU read it. The device tracks every scratch surface it hands out and disposes
the survivors when it is disposed; the pool only owns the surfaces that were returned to it, and an `Image` in a
window that is torn down with the host never returns its lease. A window closed with `Window.Close()` returns them
(see the bullets above), and the next `Image` of the same pixel size rents the pooled surface instead of allocating.

What the host refuses, on purpose: off-thread rasterization. MewUI's `CachedDeferred` mode and its size-change rebuild
ask the factory for `AcquireBackgroundRenderScope`; the embedded factory throws `NotSupportedException`, MewUI marks
the control as "async unsupported" and rasterizes synchronously from then on. `ImageVectorCacheMode.Direct` (draw the
paths on every repaint, no cache) remains available and is the right mode for a vector whose size animates every frame.
`EmbeddedUiHost.LastFrameScratchRenders` counts the scratch renders of the last frame (the tests assert on it: one per
cache on the frame it fills, zero on the frames after), `TotalScratchRenders` counts them since the device was
created, and `LiveScratchSurfaces` counts the surfaces alive (leased or pooled). The Workbench's status bar shows the
last two.

## Overlay: UI over a world

The Overlay frame draws the cube into the backbuffer first, then the `ui` pass with `clearBackground = false`, so the
color attachment LOADs and the HUD composites over the world with premultiplied alpha. With nothing under the UI (a
menu-only frame) `clearBackground = true` clears to the menu color instead; the same pass, one flag.

The HUD is a MewUI `Window` with `AllowsTransparency = true`, a transparent background and zero padding (MewUI's
default window style pads by 8 DIP). The pause menu is a second window pushed on the stack: a translucent scrim and
an opaque panel. The quit dialog is a third. The host composites the whole stack bottom to top in one frame, so the
scrim darkens the HUD and the world under it, and only the top window takes input.

The HUD is laid out at a fixed 1920 x 1080 DIP and scaled uniformly to fit the backbuffer, letterboxed on the far
edges. The pointer goes through the same scale. This is how a game HUD keeps absolute coordinates across window sizes;
vendor patch `design-space` is what makes a scale below 1 (a 1280 x 720 window) draw the whole layout instead of
clipping a third of it.

## Workbench: a viewport inside the UI

Here the UI owns the window and the 3D content is an image inside it. Per frame, before the `ui` pass,
`ViewportCompositor.AddViewportPasses` does this for every attached `ViewportControl` that is laid out and visible:

1. Size the control's `ViewportTarget` to its arranged rectangle in device pixels. A size change allocates a new
   texture and retires the old one through the device's deferred-destroy queue, so a frame still in flight never
   samples freed memory; `Generation` counts the re-allocations.
2. Import the target into the graph in `Common` and let the scene renderer (`IViewportSceneRenderer.AddPasses`) add
   its passes into it: the cube and the grid, with their own transient depth.
3. Declare the target to the UI host with `AddSampledTexture`, so the `ui` pass reads it as a shader resource and the
   compiler emits the RenderTarget to ShaderResource barrier in between.
4. `control.NotifyFrameChanged()`. MewUI's rendering is retained: an unchanged `OnRender` is not re-run, so a control
   whose image changes every frame must be invalidated every frame.

The control's `OnRender` is `context.DrawImage(image, bounds)`. The image is a MewVG image whose slot points at the
target's shader-resource view: a registry key, never a raw GPU pointer, so a stale key resolves to no image instead
of freed memory (`ExternalTextures`, `GpuTextureSource`). Nothing is copied. After the `ui` pass,
`AddTerminalPasses` adds one no-op pass per target that declares it back in `Common`, the state the next frame's
import expects, like a swapchain image after present.

Sizing uses the previous frame's layout, because layout runs inside the `ui` pass. A resize shows the old target
stretched for one frame and the new one from the next. That is the accepted shape.

Two viewports in one window prove the per-target bookkeeping; the Workbench's second viewport is a second camera on
the same scene. A secondary OS window (the detached stats panel) is a second `EmbeddedUiHost` built on the first
one's graphics factory, with its own surface and stack, rendered inside the owner's frame on the same scheduler.

## The renderer

`NanoVgRenderer` is the GPU side of the vendored MewVG, over `IGraphicsRecorder`. The rules it keeps, each of which
has a test on lavapipe:

- A fill arrives as a triangle list, not a fan. The vendored MewVG runs a real tessellator; re-fanning is invisible on
  a convex shape and destroys every concave fill and every glyph outline.
- A convex fill draws body and fringe directly. A concave one draws once through the stencil (equal 0, increment),
  so no pixel is written twice, then clears the stencil bits over its bounds. Same draw count as stencil-then-cover,
  no render-target switch inside the pass.
- Clipping is a stencil bit with exclusion polarity, taken at the coverage-0.5 contour, fringe rows included.
  Without the fringe rows every clip shrinks by half a device pixel.
- A fill of two thousand points and more arrives as an 8-bit coverage mask from MewVG's own rasterizer; the renderer
  draws a bounds quad with the paint multiplied by the mask.
- Blending is premultiplied source-over for everything. Composite modes other than source-over are not honored.
- Glyph-atlas and mask uploads are recorded on the frame's command list before the render pass opens; a copy is not
  allowed inside one.
- The steady state allocates nothing: a replayed frame with text, gradients, images and dashed strokes is 0 B on the
  managed heap (the allocation gates).

The shader is one HLSL file, cooked to DXIL and SPIR-V and committed (docs/testing.md says how to re-cook). The DX12
and Vulkan backends differ in clip-space Y, which the factory takes as a flag; the renderer and the UI never see it.

## Input and gamepad

`SdlInputBridge` turns the per-frame `RawInputSnapshot` into MewUI events for the top window: mouse moves, buttons,
wheel, key down and up with modifiers, and text, scaled from pixels to DIPs by the host's render scale. The bridge
diffs edges, so a held key is one key-down, and it suppresses the text that follows a handled key-down (an Escape that
closed a menu does not also type a character).

`GamepadFocusNavigator` moves keyboard focus with the D-pad and sticks, picking the next focusable by direction with
the rules in `MewStage.Ui.Navigation` (a window claims the gamepads, a value control such as a slider engages before
it steps, a hover parks the focus so the highlight comes back where it was), and runs the `NavCommands` for the face buttons. A screen registers one handler per command on its
window; a button binds the same command, so a key, a pad button and a click run one handler and a button without a
handler grays out by itself.
