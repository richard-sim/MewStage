using System;
using System.Globalization;
using System.IO;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Demo.Tests.Support;
using MewStage.Demo.Workbench;
using MewStage.Host;
using MewStage.Ui.Backend;
using MewStage.Viewport;

using SDL;

using Xunit;

namespace MewStage.Demo.Tests.Lavapipe;

/// <summary>
/// Workbench mode end to end on lavapipe: the real app offscreen at 1280 x 720 with a docked tool UI around offscreen
/// viewports. Each journey is an evidence row of the handoff's feature table (viewport as external image, two
/// viewports, resize re-allocates, terminal pass, MewDock persistence, secondary OS window, MewCharts, Svg).
/// </summary>
[Collection(DemoLavapipeGate.Collection)]
public sealed class WorkbenchJourneys
{
    private static string S(FormattableString s)
    {
        return s.ToString(CultureInfo.InvariantCulture);
    }

    private static int Max((int R, int G, int B) p)
    {
        return Math.Max(p.R, Math.Max(p.G, p.B));
    }

    /// <summary>The pixel rect of <paramref name="element"/> in the window.</summary>
    private static (int X, int Y, int W, int H) PixelRect(Journey j, UIElement element)
    {
        var window = (Window)element.FindVisualRoot()!;
        Rect r = element.TranslateRect(new Rect(0, 0, element.Bounds.Width, element.Bounds.Height), window);
        double k = j.UiScale;
        return ((int)Math.Round(r.X * k), (int)Math.Round(r.Y * k), (int)Math.Round(r.Width * k), (int)Math.Round(r.Height * k));
    }

    private static WorkbenchMode Enter(Journey j, int frames = 4)
    {
        j.Frames(frames);
        return Assert.IsType<WorkbenchMode>(j.App.Mode);
    }

    private static void UseTempSettings(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "mewstage-tests", name + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        UserSettings.Directory = dir;
    }

    [SkippableFact]
    public void Workbench_ViewportIsAnExternalImage_WithTheCubeInside()
    {
        DemoLavapipeGate.Require();
        UseTempSettings("viewport");
        using var j = new Journey(DemoMode.Workbench);
        Console.WriteLine(S($"[workbench] external textures registered before entering: {ExternalTextures.Count}"));
        WorkbenchMode mode = Enter(j);
        Assert.False(j.App.Host.Ui.Host!.UseDesignSpace);
        Assert.False(j.App.Host.Ui.Bridge.FocusFollowsMouse);
        Assert.True(j.App.Host.Ui.Bridge.TabNavigation);

        ViewportCompositor comp = mode.Compositor!;
        Assert.Equal(2, comp.Count);
        Assert.Equal(1, comp.RenderedLastFrame);   // Viewport 2 is not in the layout yet
        ViewportTarget target = comp.GetTarget(mode.ViewportA);
        Assert.InRange(target.Generation, 1, 3);   // the dock's first layouts can re-size the pane once or twice before it settles
        Assert.Equal(mode.ViewportA.TargetPixelWidth, target.Width);
        Assert.Equal(mode.ViewportA.TargetPixelHeight, target.Height);
        Assert.Same(target.Image, mode.ViewportA.Image);
        Assert.True(mode.ViewportA.FrameChangeCount >= 3);
        int icons = j.App.Host.Ui.Host!.LiveScratchSurfaces;   // the toolbar's and outliner's cached SVG icons
        Console.WriteLine(S($"[workbench] registered after entering: {ExternalTextures.Count} ({icons} icon caches); A gen {target.Generation}, B gen {comp.GetTarget(mode.ViewportB).Generation}"));
        Assert.True(icons >= 8, S($"the toolbar's eight SVG icons should be cached scratch surfaces, got {icons}"));
        Assert.Equal(1, ExternalTextures.Count - icons);   // the one viewport target is the only other registration

        // Steady state: the icons blit from their caches, nothing rasterizes, and the status bar says so.
        int rasterized = j.App.Host.Ui.Host!.TotalScratchRenders;
        j.Frames(3);
        Assert.Equal(rasterized, j.App.Host.Ui.Host!.TotalScratchRenders);
        Assert.Equal(0, j.App.Host.Ui.Host!.LastFrameScratchRenders);
        Assert.Contains(S($"UI caches {icons}, rasterized {rasterized}x"), mode.ViewModel.Status.Value, StringComparison.Ordinal);

        // The viewport rect shows the world: a dark tinted background near its corner, cube pixels around its center,
        // and the UI chrome around it is not the viewport's color.
        (int vx, int vy, int vw, int vh) = PixelRect(j, mode.ViewportA);
        Console.WriteLine(S($"[workbench] viewport px rect ({vx}, {vy}, {vw}, {vh}); target {target.Width} x {target.Height}"));
        Assert.True(vw > 300 && vh > 200, "the viewport pane is too small to judge");
        (int R, int G, int B) corner = j.Readback.Px(vx + 6, vy + 6);
        (double R, double G, double B) center = j.Readback.Mean(vx + (vw / 2) - 8, vy + (vh / 2) - 24, 16, 16);
        Console.WriteLine(S($"[workbench] viewport corner={corner} center={center}"));
        Assert.True(Max(corner) < 60, S($"the viewport corner is not the world background: {corner}"));
        Assert.True(Math.Max(center.R, Math.Max(center.G, center.B)) > Max(corner) + 40, S($"no cube in the viewport: {center} vs {corner}"));

        // The menu bar, toolbar icons and the stats chart are drawn around it.
        int menuText = j.Readback.Count(0, 0, 400, 30, p => Max(p) > 170);
        (int sx, int sy, int sw, int sh) = PixelRect(j, mode.Stats!.FrameChart);
        int chartInk = j.Readback.Count(sx + 2, sy + 2, Math.Max(1, sw - 4), Math.Max(1, sh - 4), p => Max(p) > 90);
        Console.WriteLine(S($"[workbench] menu text px={menuText} chart ink px={chartInk} in ({sx}, {sy}, {sw}, {sh}) samples={mode.Stats.Samples}"));
        Assert.True(menuText > 60, "the menu bar text is missing");
        Assert.True(mode.Stats.Samples >= 3);
        Assert.True(chartInk > 40, "the stats chart drew nothing");
        j.Readback.Dump("workbench");

        // Leaving releases the viewport target and closes the window: no viewport texture remains registered, and the
        // icon caches went back to the scratch pool (still alive, by design: a re-entered Workbench reuses them).
        mode.ViewModel.Viewport2Visible.Set(false);
        j.App.Switch(new StartPage());
        j.Frames(2);
        Assert.IsType<StartPage>(j.App.Mode);
        Assert.Null(mode.Window);
        int pooled = j.App.Host.Ui.Host!.LiveScratchSurfaces;
        Console.WriteLine(S($"[workbench] after leaving: {ExternalTextures.Count} registered, {pooled} scratch surfaces alive"));
        Assert.Equal(0, ExternalTextures.Count - pooled);   // no viewport target remains
        Assert.Equal(icons, pooled);                         // the closed window returned its icons, none leaked or lost

        // Re-entering builds a new window whose icons rent the pooled surfaces: the count does not grow, and each icon
        // rasterizes once into its rented surface.
        int beforeReentry = j.App.Host.Ui.Host!.TotalScratchRenders;
        j.App.Switch(new WorkbenchMode());
        WorkbenchMode again = Enter(j);
        int rerendered = j.App.Host.Ui.Host!.TotalScratchRenders - beforeReentry;
        Console.WriteLine(S($"[workbench] re-entered: {j.App.Host.Ui.Host!.LiveScratchSurfaces} scratch surfaces alive, {rerendered} rasterized"));
        Assert.Equal(icons, j.App.Host.Ui.Host!.LiveScratchSurfaces);
        Assert.Equal(icons, rerendered);
        again.ViewModel.Viewport2Visible.Set(false);
        j.App.Switch(new StartPage());
        j.Frames(2);
        Assert.Equal(icons, j.App.Host.Ui.Host!.LiveScratchSurfaces);

        // Trimming drops the pool: nothing of the Workbench stays registered.
        j.App.Host.Ui.Host!.TrimCaches();
        Assert.Equal(0, j.App.Host.Ui.Host!.LiveScratchSurfaces);
        Assert.Equal(0, ExternalTextures.Count);
        j.Frames(2);   // the start page still renders after the trim
        Assert.Equal(0, ExternalTextures.Count);
    }

    [SkippableFact]
    public void Workbench_TwoViewports_TwoTargets_BothDrawn()
    {
        DemoLavapipeGate.Require();
        UseTempSettings("two");
        using var j = new Journey(DemoMode.Workbench);
        WorkbenchMode mode = Enter(j);
        mode.SetViewport2(true);
        Assert.True(mode.ViewModel.Viewport2Visible.Value);
        j.Frames(3);
        ViewportCompositor comp = mode.Compositor!;

        // Both tabs share one group: the active tab is the one laid out. Activate each and check it renders.
        Assert.Equal(1, comp.RenderedLastFrame);
        ViewportTarget a = comp.GetTarget(mode.ViewportA);
        ViewportTarget b = comp.GetTarget(mode.ViewportB);
        Assert.True(b.Generation >= 1 || a.Generation >= 1);
        Assert.Equal(2, ExternalTextures.Count - j.App.Host.Ui.Host!.LiveScratchSurfaces);   // two targets besides the icon caches
        Assert.NotSame(a.Color, b.Color);

        // Viewport 2 in a split of its own: both laid out, both rendered.
        mode.Dock!.DocumentPanes[1].SplitOff(Aprillz.MewUI.MewDock.DockEdge.Right);
        j.Frames(3);
        Assert.Equal(2, comp.RenderedLastFrame);
        Assert.True(a.Generation >= 1 && b.Generation >= 1, S($"generations A={a.Generation} B={b.Generation}"));
        (int bx, int by, int bw, int bh) = PixelRect(j, mode.ViewportB);
        (double R, double G, double B) bCorner = j.Readback.Mean(bx + 4, by + 4, 48, 48);
        Console.WriteLine(S($"[two] A {a.Width}x{a.Height} gen {a.Generation}; B {b.Width}x{b.Height} gen {b.Generation} at ({bx}, {by}, {bw}, {bh}); B corner {bCorner}"));
        j.Readback.Dump("workbench-two");
        Assert.True(bCorner.R > bCorner.G && bCorner.B > bCorner.G && bCorner.B < 90, S($"viewport 2's purple-tinted background is missing: {bCorner}"));

        mode.SetViewport2(false);
        j.Frames(2);
        Assert.Equal(1, comp.RenderedLastFrame);
        Assert.False(mode.ViewModel.Viewport2Visible.Value);
    }

    [SkippableFact]
    public void Workbench_Resize_ReallocatesTheTarget()
    {
        DemoLavapipeGate.Require();
        UseTempSettings("resize");
        using var j = new Journey(DemoMode.Workbench);
        WorkbenchMode mode = Enter(j);
        ViewportTarget target = mode.Compositor!.GetTarget(mode.ViewportA);
        int gen = target.Generation;
        (int w0, int h0) = (target.Width, target.Height);

        j.Resize(1024, 640);
        j.Frames(2);
        Assert.Equal(1024, j.App.Host.Gpu.PixelWidth);
        Console.WriteLine(S($"[resize] target {w0}x{h0} gen {gen} -> {target.Width}x{target.Height} gen {target.Generation}"));
        Assert.True(target.Generation > gen, "the viewport target was not re-allocated after a window resize");
        Assert.True(target.Width < w0, "the viewport target did not shrink with the window");
        Assert.Equal(mode.ViewportA.TargetPixelWidth, target.Width);
        Assert.Same(target.Image, mode.ViewportA.Image);
    }

    [SkippableFact]
    public void Workbench_DockLayout_SavesAndRestoresAcrossRuns()
    {
        DemoLavapipeGate.Require();
        UseTempSettings("layout");
        string file = UserSettings.PathFor(WorkbenchMode.LayoutFile);
        using (var j = new Journey(DemoMode.Workbench))
        {
            WorkbenchMode mode = Enter(j);
            Assert.False(mode.ViewModel.Viewport2Visible.Value);
            mode.SetViewport2(true);
            j.Frames(2);
            j.App.Switch(new StartPage());   // leaving saves
            j.Frames(2);
        }

        Assert.True(File.Exists(file), "the layout file was not written on leave");
        string json = File.ReadAllText(file);
        Assert.Contains("\"viewport2\"", json, StringComparison.Ordinal);

        using (var j = new Journey(DemoMode.Workbench))
        {
            WorkbenchMode mode = Enter(j);
            Assert.True(mode.ViewModel.Viewport2Visible.Value, "the restored layout did not bring Viewport 2 back");
            Assert.Equal(2, mode.Dock!.DocumentPanes.Count);
            mode.ResetLayout();
            j.Frames(2);
            Assert.False(mode.ViewModel.Viewport2Visible.Value);
            Assert.Single(mode.Dock.DocumentPanes);
        }
    }

    [SkippableFact]
    public void Workbench_StatsWindow_DetachesIntoASecondaryWindow_AndDocksBack()
    {
        DemoLavapipeGate.Require();
        UseTempSettings("stats");
        using var j = new Journey(DemoMode.Workbench);
        WorkbenchMode mode = Enter(j);
        Assert.Equal(1, j.App.Host.WindowCount);
        long before = mode.Stats!.Samples;

        mode.ToggleStatsWindow();
        Assert.NotNull(mode.StatsWindow);
        Assert.Equal(2, j.App.Host.WindowCount);
        using var rb = new FrameReadback(j.App.Host.Gpu.Device);
        mode.StatsWindow.BeforePresent = rb.Record;
        j.Frames(4);
        Assert.True(mode.StatsWindow.FramesPresented >= 3);
        Assert.True(mode.Stats.Samples > before);
        int ink = rb.Count(p => Max(p) > 90);
        Console.WriteLine(S($"[stats-window] {rb.Width}x{rb.Height}, {mode.StatsWindow.FramesPresented} frames, ink px={ink}"));
        Assert.True(ink > 200, "the detached stats window shows no chart");
        Assert.DoesNotContain(mode.Dock!.Panes, p => p.Component == "stats");
        rb.Dump("workbench-stats-window");

        // The window's own ComboBox opens inside its surface (a popup in a secondary OS window).
        mode.Stats.Range.IsDropDownOpen = true;
        j.Frames(2);
        Assert.True(mode.Stats.Range.IsDropDownOpen);
        mode.Stats.Range.IsDropDownOpen = false;

        mode.ToggleStatsWindow();
        j.Frames(2);
        Assert.Null(mode.StatsWindow);
        Assert.Equal(1, j.App.Host.WindowCount);
        Assert.Contains(mode.Dock.Panes, p => p.Component == "stats");
    }

    [SkippableFact]
    public void Workbench_DevToolsKeys_InWindowToolsDraw_WindowOpeningOnesDoNothing()
    {
        DemoLavapipeGate.Require();
        UseTempSettings("devtools");
        using var j = new Journey(DemoMode.Workbench);
        WorkbenchMode mode = Enter(j);
        Window window = mode.Window!;
#if DEBUG
        WindowDevTools tools = Assert.IsType<WindowDevTools>(window.DevTools);

        // Ctrl+Shift+P: the performance monitor draws into our surface (top-right) through the adorner layer.
        int before = j.Readback.Count(j.Readback.Width - 360, 0, 360, 160, p => Max(p) > 150);
        j.Key(SDL_Scancode.SDL_SCANCODE_LCTRL, true);
        j.Key(SDL_Scancode.SDL_SCANCODE_LSHIFT, true);
        j.Frames(1);
        j.Tap(SDL_Scancode.SDL_SCANCODE_P);
        Assert.True(tools.PerformanceMonitorIsVisible, "Ctrl+Shift+P did not open the performance monitor");
        j.Frames(3);
        int after = j.Readback.Count(j.Readback.Width - 360, 0, 360, 160, p => Max(p) > 150);
        Console.WriteLine(S($"[devtools] bright px top-right: {before} -> {after} with the performance monitor on"));
        Assert.True(after > before + 150, "the performance monitor drew nothing");
        j.Readback.Dump("workbench-devtools-perf");

        // Ctrl+Shift+T (the visual tree, an OS window of its own) is unmapped on the embedded host: nothing happens,
        // and the Workbench's own Stats Window gesture lives on Ctrl+Shift+S so the two never collide.
        j.Tap(SDL_Scancode.SDL_SCANCODE_T);
        Assert.False(tools.VisualTreeIsOpen);
        Assert.Null(mode.StatsWindow);
        Assert.Equal(1, j.App.Host.WindowCount);
        j.Tap(SDL_Scancode.SDL_SCANCODE_P);
        Assert.False(tools.PerformanceMonitorIsVisible);
        j.Tap(SDL_Scancode.SDL_SCANCODE_S);   // the new window takes the keyboard from here on
        Assert.NotNull(mode.StatsWindow);
        Assert.Equal(2, j.App.Host.WindowCount);
        j.Key(SDL_Scancode.SDL_SCANCODE_LSHIFT, false);
        j.Key(SDL_Scancode.SDL_SCANCODE_LCTRL, false);
        j.Frames(2);
        mode.ToggleStatsWindow();
        j.Frames(2);
        Assert.Equal(1, j.App.Host.WindowCount);
#else
        Assert.Null(window.DevTools);
        j.Frames(1);
#endif
    }

    [SkippableFact]
    public void Workbench_ViewportGestures_OrbitZoomAndFrame()
    {
        DemoLavapipeGate.Require();
        UseTempSettings("gestures");
        using var j = new Journey(DemoMode.Workbench);
        WorkbenchMode mode = Enter(j);
        (int vx, int vy, int vw, int vh) = PixelRect(j, mode.ViewportA);
        float cx = vx + (vw / 2f);
        float cy = vy + (vh / 2f);
        float yaw0 = mode.CameraA.Yaw;
        float dist0 = mode.CameraA.Distance;

        j.Drag(cx, cy, cx + 120, cy + 10);
        Assert.NotEqual(yaw0, mode.CameraA.Yaw);
        Assert.Same(mode.ViewportA, mode.Window!.FocusManager.FocusedElement);   // the press focused the viewport

        j.Wheel(2f, cx, cy);
        Assert.True(mode.CameraA.Distance < dist0, S($"the wheel did not zoom in: {dist0} -> {mode.CameraA.Distance}"));

        mode.CameraA.Distance = 20f;
        j.Tap(SDL_Scancode.SDL_SCANCODE_F);
        Assert.True(mode.CameraA.Distance < 10f, S($"F did not frame the cube: {mode.CameraA.Distance}"));

        // Middle-drag pans the target.
        System.Numerics.Vector3 target0 = mode.CameraA.Target;
        j.Drag(cx, cy, cx + 60, cy, steps: 3, button: 2);
        Assert.NotEqual(target0, mode.CameraA.Target);
    }

    [SkippableFact]
    public void Workbench_InspectorEdits_ReachTheScene_AndAboutOpensInTheOverlay()
    {
        DemoLavapipeGate.Require();
        UseTempSettings("inspector");
        using var j = new Journey(DemoMode.Workbench);
        WorkbenchMode mode = Enter(j);
        Inspector inspector = mode.InspectorPanel!;

        // The outliner selection shows the matching inspector panel.
        mode.ViewModel.SelectedIndex.Set(1);
        j.Frames(1);
        Assert.False(inspector.CubePanel.IsVisible);
        Assert.True(inspector.LightPanel.IsVisible);
        mode.ViewModel.SelectedIndex.Set(0);
        j.Frames(1);
        Assert.True(inspector.CubePanel.IsVisible);
        Assert.False(inspector.LightPanel.IsVisible);

        // A click on the color box opens its list; Escape closes it; a view-model change reaches the shared scene.
        j.Click(inspector.Colors);
        Assert.True(inspector.Colors.IsDropDownOpen);
        j.Tap(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        Assert.False(inspector.Colors.IsDropDownOpen);
        mode.ViewModel.ColorIndex.Set(1);
        mode.ViewModel.Wireframe.Set(true);
        j.Frames(2);
        Assert.Equal(CubeColors.Tint(1), mode.World.Settings.Tint);
        Assert.True(mode.World.Settings.Wireframe);
        Assert.Equal(true, inspector.Wireframe.IsChecked);

        // Typing a spin speed commits through the text box into the slider.
        j.Click(inspector.SpinText);
        Assert.Same(inspector.SpinText, mode.Window!.FocusManager.FocusedElement);
        j.Tap(SDL_Scancode.SDL_SCANCODE_END);
        for (int i = 0; i < 4; i++)
        {
            j.Tap(SDL_Scancode.SDL_SCANCODE_BACKSPACE);
        }

        j.Type("2.5");
        j.Tap(SDL_Scancode.SDL_SCANCODE_RETURN);
        j.Frames(1);
        Console.WriteLine(S($"[inspector] spin text '{inspector.SpinText.Text}' -> vm {mode.ViewModel.SpinSpeed.Value} slider {inspector.SpinSlider.Value}"));
        Assert.Equal(2.5, mode.ViewModel.SpinSpeed.Value, 3);
        Assert.Equal(2.5, inspector.SpinSlider.Value, 3);

        // About lives in the window's overlay layer: a panel in the middle, Escape closes it.
        mode.ShowAbout();
        Assert.True(mode.IsAboutOpen);
        j.Frames(2);
        (int R, int G, int B) mid = j.Readback.Px(640, 360);
        Console.WriteLine(S($"[about] center pixel {mid}"));
        Assert.True(Math.Abs(mid.R - 36) < 16 && Math.Abs(mid.G - 39) < 16 && Math.Abs(mid.B - 48) < 16, S($"the About panel is not at the center: {mid}"));
        j.Readback.Dump("workbench-about");
        j.Tap(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        Assert.False(mode.IsAboutOpen);
    }
}
