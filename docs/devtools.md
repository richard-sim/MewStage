# DevTools, Hot Reload and Preview

MewUI ships four development tools and two development workflows. Which of them work depends on which host the
UI is running on, because two of the tools open windows of their own and the workflows need a JIT process. This
page says what works where in MewStage, how to start each one, and what to expect.

## The two hosts

The demo runs MewUI on two different hosts, and the choice is the whole point of the project.

- **The embedded host** (Overlay and Workbench modes). The app owns the SDL window, the GPU device and the render
  graph; MewUI draws into one pass of that graph through MewStage.Ui.Backend. There is no MewUI `Application` and no
  MewUI platform window.
- **The platform host** (Kit mode, `--standard`). MewUI's own Win32 or X11 platform host owns the window and the
  message loop, and the MewVG OpenGL backend draws. No SDL, no app device, no render graph. This is the configuration
  upstream's tooling is written for.

Run the platform host with

    dotnet run --project src/MewStage.Demo -- --standard

or from the start page's Kit button, which restarts the demo with that flag (a MewUI platform host has to own the
process's UI loop from the start, so it cannot be entered from a running SDL window).

## The DevTools

| Tool | Shortcut | Embedded host | Platform host |
| --- | --- | --- | --- |
| Element inspector | Ctrl+Shift+I | yes | yes |
| Performance monitor | Ctrl+Shift+P | yes | yes |
| Visual tree window | Ctrl+Shift+T | unmapped | yes |
| Profiler timeline | Ctrl+Shift+Alt+P | unmapped | yes |

The inspector and the performance monitor draw into the window they belong to, through MewUI's adorner layer, so
they come out of our backend like any other element: in the Workbench they are composited into the same render-graph
pass as the dock and the viewport image. The visual tree and the profiler open OS windows of their own, which only a
platform host can do, so the embedded host removes their two gestures from every window it attaches
(`EmbeddedUiHost.UnmapWindowOpeningDevTools`). Pressing them in the Overlay or the Workbench does nothing. The
Workbench's own Stats Window command is on Ctrl+Shift+S for the same reason.

The tools exist only in a Debug JIT build. `MewUIDevTools` defaults to true in Debug and false in Release, and a
trimmed or NativeAOT publish never contains them (the property panel lists members by reflection, and a trimmed list
would quietly lie). `WindowDevTools.IsSupported` says which kind of build is running; the Kit page's status bar prints
it. The embedded host's test projects set the property themselves (`MewStage.Demo.Tests` turns it on in Debug so the
gate is tested both ways).

On the embedded host the performance monitor's "Loop" line reads "(not running)": that line describes MewUI's own
render loop, which the embedded host replaces with the app's frame. The per-frame numbers above it are MewUI's own
window-frame timings; they are filled in on both hosts as the window renders frames, so a window that has nothing
to redraw shows the numbers of the frame the monitor appeared in until something changes.

## Hot Reload

Hot Reload needs a JIT process and MewUI's own handler, which is registered by the framework; no declaration is
needed in the app. Run the Kit under `dotnet watch`:

    dotnet watch run --project src/MewStage.Demo -- --standard

MewUI rebuilds only the nodes whose build function changed. The Kit page composes its content inside
`Window.Build(...)` (see `src/MewStage.Demo/Kit/KitPage.cs`), which is the composition-site idiom the handler looks
for, so an edit inside `KitPage.Compose` re-composes the window on save and keeps the view models (they are
constructed outside the callback). An edit to a helper in another type, such as one of the tab builders in
`KitSamples`, is applied to the process but does not re-compose the window by itself; edit something in `KitPage` as
well, or restart. Event handler bodies apply on the next event with no rebuild at all.

Hot Reload also works for the Overlay and Workbench windows in principle (it is a runtime feature), but their build
code is not registered through `Window.Build(...)`, so an edit there needs a restart. Those modes are where the
app's own code lives; the Kit is the place to iterate on controls and styles.

## Editor Preview

The MewUI editor preview (the VS Code extension) runs the executable's `Main` in full and shows every `Window` and
`UserControl` type it can construct in an editor panel, updating through Hot Reload on save. `Design.IsPreviewMode`
is set before `Main` runs, and the demo checks it first: under the preview the demo always runs Kit mode, whatever
the arguments, because an SDL window from the other modes would open on screen outside the panel.

The executable references the preview support project (`MewUI.Preview`), so the target dropdown offers the Kit
window; the mode classes of the Overlay and the Workbench are not windows and do not appear.

## Checking Kit mode without a display

`scripts/kit-smoke-x11.sh` starts an Xvfb screen, runs the built Debug demo on it with `--standard`, clicks through
every tab, opens and closes each DevTool by its shortcut (counting the visual tree and profiler windows by title),
and fails if the process dies at any step. With `MEWSTAGE_JOURNEY_OUT` set and ImageMagick installed it also writes
a screenshot of every step. It needs Xvfb, xdotool and Mesa's software GL (`LIBGL_ALWAYS_SOFTWARE=1` is set for the
run). CI runs it on Linux; on Windows the same checks are a hand test (docs/hand-test.md, Phase 7).
