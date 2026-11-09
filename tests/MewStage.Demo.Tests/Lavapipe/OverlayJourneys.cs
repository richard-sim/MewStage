using System;
using System.Collections.Generic;
using System.Globalization;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Demo.Overlay;
using MewStage.Demo.Tests.Support;

using SDL;

using Xunit;

namespace MewStage.Demo.Tests.Lavapipe;

/// <summary>
/// Overlay mode end to end on lavapipe: the real app offscreen at 1280 x 720, driven by SDL events and read back pixel
/// by pixel. Each journey is the evidence row of the handoff's feature table it is named after; PNG dumps land in
/// <c>MEWSTAGE_JOURNEY_OUT</c> when set.
/// </summary>
[Collection(DemoLavapipeGate.Collection)]
public sealed class OverlayJourneys
{
    private static string S(FormattableString s)
    {
        return s.ToString(CultureInfo.InvariantCulture);
    }

    private static int Sum((int R, int G, int B) p)
    {
        return p.R + p.G + p.B;
    }

    private static int Max((int R, int G, int B) p)
    {
        return Math.Max(p.R, Math.Max(p.G, p.B));
    }

    /// <summary>A pixel just inside the bottom-left corner of <paramref name="panel"/> (blank panel color).</summary>
    private static (int X, int Y) InsidePanel(Journey j, Border panel)
    {
        var window = (Window)panel.FindVisualRoot()!;
        Rect r = panel.TranslateRect(new Rect(0, 0, panel.Bounds.Width, panel.Bounds.Height), window);
        return j.Design(r.X + 14, r.Bottom - 14);
    }

    private static OverlayMode EnterOverlay(Journey j)
    {
        j.Frames(3);
        return Assert.IsType<OverlayMode>(j.App.Mode);
    }

    [SkippableFact]
    public void Overlay_WorldUnderTheHud_AndTheReadoutsChangeEveryFrame()
    {
        DemoLavapipeGate.Require();
        using var j = new Journey(DemoMode.Overlay);
        OverlayMode mode = EnterOverlay(j);
        FrameReadback rb = j.Readback;
        Assert.True(j.App.Host.Ui.Host is { UseDesignSpace: true });
        Assert.Equal(1280.0 / 1920.0, j.UiScale, 4);

        // The world shows through the transparent HUD: the cube is lit in the middle, the sky stays dark up top.
        (double R, double G, double B) center = rb.Mean(632, 352, 16, 16);
        (double R, double G, double B) sky = rb.Mean(632, 10, 16, 10);
        int skyMax = (int)Math.Max(sky.R, Math.Max(sky.G, sky.B));
        int centerMax = (int)Math.Max(center.R, Math.Max(center.G, center.B));
        Console.WriteLine(S($"[overlay] center={center} sky={sky}"));
        Assert.True(skyMax < 60, S($"the sky is not dark: {sky}"));
        Assert.True(centerMax > skyMax + 40, S($"no cube in the middle of the world: center {center} vs sky {sky}"));

        // The HUD composites over it: bright title text top-left, the legend top-right, the marker bottom-right.
        int title = rb.Count(0, 0, 600, 160, p => Max(p) > 200);
        int legend = rb.Count(900, 0, 380, 160, p => Max(p) > 200);
        (int mx, int my) = j.Design(HudView.DesignWidth - 48 - 260, HudView.DesignHeight - 36 - 26);
        int marker = rb.Count(mx, my, 260 * 2 / 3, 24, p => Max(p) > 120);
        Console.WriteLine(S($"[overlay] title={title} legend={legend} marker={marker}"));
        Assert.True(title > 150, S($"HUD title text missing ({title} bright pixels)"));
        Assert.True(legend > 80, S($"HUD legend missing ({legend} bright pixels)"));
        Assert.True(marker > 20, S($"far-corner marker missing ({marker} lit pixels)"));

        // Retained render: a readout string changes (the bound setter invalidates) and the HUD shows the new value.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 60; i++)
        {
            j.Frames(1);
            seen.Add(mode.ViewModel.Angle.Value + "|" + mode.ViewModel.FrameTime.Value);
        }

        Assert.Equal(mode.ViewModel.FrameTime.Value, mode.Hud!.FrameTimeText.Text);
        Console.WriteLine(S($"[overlay] distinct readouts over 60 frames: {seen.Count}; angle={mode.World.Angle:F3}"));
        Assert.True(seen.Count > 40, S($"the readouts barely changed over 60 frames ({seen.Count} distinct)"));
        Assert.True(mode.Scene!.FramesDrawn >= 63);
        rb.Dump("overlay-hud");
    }

    [SkippableFact]
    public void Overlay_PauseAndDialog_StackCompositesAndOnlyTheTopTakesInput()
    {
        DemoLavapipeGate.Require();
        using var j = new Journey(DemoMode.Overlay);
        OverlayMode mode = EnterOverlay(j);
        FrameReadback rb = j.Readback;
        (int wx, int wy) = (200, 500);   // a world pixel away from the HUD and the panels
        (int R, int G, int B) worldBefore = rb.Px(wx, wy);
        float angleBefore = mode.World.Angle;

        // Escape opens the pause menu: the scrim darkens the world, the panel is opaque, the cube stops.
        j.Tap(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        Assert.True(mode.IsPaused);
        j.Frames(2);
        Assert.Same(mode.Pause!.Window, j.App.Host.Ui.TopWindow);
        (int R, int G, int B) worldUnderScrim = rb.Px(wx, wy);
        (int px, int py) = InsidePanel(j, mode.Pause.Panel);
        (int R, int G, int B) panel = rb.Px(px, py);
        Console.WriteLine(S($"[pause] world {worldBefore} -> {worldUnderScrim}; panel {panel} at ({px}, {py})"));
        Assert.True(Sum(worldUnderScrim) < Sum(worldBefore) - 12, S($"the scrim did not darken the world: {worldBefore} -> {worldUnderScrim}"));
        Assert.True(Math.Abs(panel.R - 30) < 14 && Math.Abs(panel.G - 33) < 14 && Math.Abs(panel.B - 41) < 14, S($"the pause panel is not opaque panel color: {panel}"));
        float anglePaused = mode.World.Angle;
        j.Frames(3);
        Assert.Equal(anglePaused, mode.World.Angle);
        Assert.NotEqual(angleBefore, anglePaused);
        rb.Dump("overlay-pause");

        // Quit opens the dialog above: it fades in (element opacity 0 -> 1 under the host's pulses) over the paused menu.
        j.Click(mode.Pause.QuitButton);
        Assert.NotNull(mode.Dialog);
        QuitDialog dialog = mode.Dialog;
        Assert.Same(dialog.Window, j.App.Host.Ui.TopWindow);
        int fadeFrames = 0;
        while (dialog.Root.Opacity < 1.0 && fadeFrames < 60)
        {
            j.Frames(1);
            fadeFrames++;
        }

        Console.WriteLine(S($"[dialog] faded in over {fadeFrames} frames"));
        Assert.Equal(1.0, dialog.Root.Opacity);
        Assert.True(fadeFrames >= 1, "the dialog appeared fully opaque without a fade");
        j.Frames(1);
        (int R, int G, int B) panelUnderDialog = rb.Px(px, py);
        Assert.True(Sum(panelUnderDialog) < Sum(panel) - 12, S($"the dialog's scrim did not darken the pause panel: {panel} -> {panelUnderDialog}"));
        Grid dialogRoot = dialog.Root;
        var dialogPanel = (Border)dialogRoot.Children[1];
        (int dx, int dy) = InsidePanel(j, dialogPanel);
        (int R, int G, int B) dpanel = rb.Px(dx, dy);
        Console.WriteLine(S($"[dialog] pause panel {panel} -> {panelUnderDialog}; dialog panel {dpanel}"));
        Assert.True(Math.Abs(dpanel.R - 36) < 14 && Math.Abs(dpanel.G - 39) < 14 && Math.Abs(dpanel.B - 48) < 14, S($"the dialog panel is not its color: {dpanel}"));
        rb.Dump("overlay-dialog");

        // Only the top window takes input: the pause menu's Resume button under the dialog does nothing.
        j.Click(mode.Pause.Resume);
        Assert.True(mode.IsPaused);
        Assert.NotNull(mode.Dialog);

        // Escape backs out one window at a time: dialog, then menu.
        Window dialogWindow = mode.Dialog.Window;
        Window pauseWindow = mode.Pause.Window;
        j.Tap(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        Assert.Null(mode.Dialog);
        Assert.True(mode.IsPaused);
        Assert.Same(mode.Pause.Window, j.App.Host.Ui.TopWindow);
        j.Tap(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        Assert.False(mode.IsPaused);
        Assert.Same(mode.Hud!.Window, j.App.Host.Ui.TopWindow);
        j.Frames(2);
        Assert.NotEqual(anglePaused, mode.World.Angle);   // spinning again

        // The popped windows were closed at the next Tick, outside their own key handlers; the HUD is still open.
        Assert.Equal(0, (int)dialogWindow.Handle);
        Assert.Equal(0, (int)pauseWindow.Handle);
        Assert.NotEqual(0, (int)mode.Hud.Window.Handle);
    }

    [SkippableFact]
    public void Overlay_QuitDialog_ConfirmClosesTheWindow()
    {
        DemoLavapipeGate.Require();
        using var j = new Journey(DemoMode.Overlay);
        OverlayMode mode = EnterOverlay(j);
        mode.OpenPause();
        j.Frames(1);
        mode.OpenQuitDialog();
        j.Frames(2);
        j.Click(mode.Dialog!.Quit);
        Assert.True(j.App.Host.Input.CloseRequested);
        Assert.False(j.App.RunFrame());
        Assert.Equal(0, j.App.ExitCode);
    }

    [SkippableFact]
    public void Overlay_ComboBox_OpensItsListInsideTheSurface()
    {
        DemoLavapipeGate.Require();
        using var j = new Journey(DemoMode.Overlay);
        OverlayMode mode = EnterOverlay(j);
        j.Tap(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        j.Frames(1);
        PauseMenu pause = mode.Pause!;
        Window window = pause.Window;
        Rect combo = pause.Colors.TranslateRect(new Rect(0, 0, pause.Colors.Bounds.Width, pause.Colors.Bounds.Height), window);
        (int bx, int by) = j.Design(combo.X + 8, combo.Bottom + 8);
        (int bw, int bh) = j.Design(combo.Width - 16, 60);
        int panelBefore = j.Readback.Count(bx, by, bw, bh, p => Math.Abs(p.R - 30) < 10 && Math.Abs(p.G - 33) < 10 && Math.Abs(p.B - 41) < 10);

        j.Click(pause.Colors);
        Assert.True(pause.Colors.IsDropDownOpen, "the ComboBox did not open on click");
        j.Frames(2);
        int panelAfter = j.Readback.Count(bx, by, bw, bh, p => Math.Abs(p.R - 30) < 10 && Math.Abs(p.G - 33) < 10 && Math.Abs(p.B - 41) < 10);
        Console.WriteLine(S($"[combo] panel-colored pixels below the box: {panelBefore} -> {panelAfter} of {bw * bh}"));
        Assert.True(panelBefore > bw * bh / 2, "the area below the ComboBox was not blank panel before opening");
        Assert.True(panelAfter < panelBefore / 2, "the drop-down list did not draw inside the window surface");
        j.Readback.Dump("overlay-combo");

        // Escape closes the list (the ComboBox handles it); the menu stays.
        j.Tap(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        Assert.False(pause.Colors.IsDropDownOpen);
        Assert.True(mode.IsPaused);
    }

    [SkippableFact]
    public void Overlay_TextTypedInTheMenu_ShowsOnTheHud()
    {
        DemoLavapipeGate.Require();
        using var j = new Journey(DemoMode.Overlay);
        OverlayMode mode = EnterOverlay(j);
        j.Tap(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        PauseMenu pause = mode.Pause!;
        j.Click(pause.Label);
        Assert.Same(pause.Label, pause.Window.FocusManager.FocusedElement);
        j.Type("Hello HUD");
        j.Frames(2);
        Assert.Equal("Hello HUD", pause.Label.Text);
        Assert.Equal("Hello HUD", mode.ViewModel.Label.Value);

        // Resume: the HUD shows the label under the readouts (bright text where there was none).
        (int lx, int ly) = j.Design(48, 40 + 60 + 40 + 40 + 12);
        int before = j.Readback.Count(lx, ly, 300, 36, p => Max(p) > 200);
        j.Tap(SDL_Scancode.SDL_SCANCODE_ESCAPE);
        Assert.False(mode.IsPaused);
        j.Frames(2);
        int after = j.Readback.Count(lx, ly, 300, 36, p => Max(p) > 200);
        Console.WriteLine(S($"[label] bright pixels in the label row: {before} -> {after}"));
        Assert.True(after > 60, S($"the typed label is not on the HUD ({after} bright pixels)"));
        j.Readback.Dump("overlay-label");
    }

    [SkippableFact]
    public void Overlay_Gamepad_StartOpensTheMenu_AndBClosesIt()
    {
        DemoLavapipeGate.Require();
        using var j = new Journey(DemoMode.Overlay);
        OverlayMode mode = EnterOverlay(j);
        j.TapPad(Input.GamepadButton.Start);
        Assert.True(mode.IsPaused, "Start did not open the pause menu");
        j.Frames(1);
        Assert.NotNull(mode.Pause!.Window.FocusManager.FocusedElement);   // the navigator seeded a focus

        j.TapPad(Input.GamepadButton.B);
        Assert.False(mode.IsPaused, "B did not close the pause menu");
    }

    [SkippableFact]
    public void Overlay_LetterboxedWindow_DrawsTheWholeDesignSpace()
    {
        DemoLavapipeGate.Require();
        using var j = new Journey(DemoMode.Overlay, 1024, 600);
        OverlayMode mode = EnterOverlay(j);
        Assert.Equal(1024.0 / 1920.0, j.UiScale, 4);   // k from the width; 12 px letterbox top / bottom... below the layout
        TextBlock marker = mode.Hud!.CornerMarker;
        Rect r = marker.TranslateRect(new Rect(0, 0, marker.Bounds.Width, marker.Bounds.Height), mode.Hud.Window);
        (int mx, int my) = j.Design(r.X, r.Y);
        (int mw, int mh) = j.Design(r.Width, r.Height);
        int lit = j.Readback.Count(mx, my, mw, mh, p => Max(p) > 120);
        Console.WriteLine(S($"[letterbox] marker rect px=({mx}, {my}, {mw}, {mh}) lit={lit}"));
        Assert.True(my + mh <= 576 + 2, S($"the marker fell outside the fitted design space: bottom {my + mh}"));
        Assert.True(lit > 20, S($"the far-corner marker is not drawn at k < 1 ({lit} lit pixels)"));
        j.Readback.Dump("overlay-letterbox");
    }
}
