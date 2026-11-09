using System;
using System.Globalization;
using System.Reflection;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Vulkan;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Lavapipe;

/// <summary>
/// The host contract on a real (software) GPU, driven through the production frame: the host wires its <c>ui</c>
/// pass into the application's render graph, the compiler owns the barriers, the transient pool backs the stencil,
/// and lavapipe executes it. Each test reads the pixels back and asserts what the application relies on.
/// </summary>
[Collection(UiLavapipeGate.Collection)]
public sealed class HostContractLavapipeTests
{
    private static Window Black(UIElement content)
    {
        return new Window { Padding = Thickness.Zero, Background = Color.FromArgb(255, 0, 0, 0) }.Content(content);
    }

    private static EmbeddedUiHost NewHost(VulkanDevice dev, Window window, int w, int h)
    {
        (byte[] vs, byte[] ps) = TestAssets.UiShaders();
        var host = new EmbeddedUiHost(dev, vs, ps);
        host.Resize(w, h, 1.0);
        host.SetWindows([window]);
        return host;
    }

    private static string S(FormattableString s)
    {
        return s.ToString(CultureInfo.InvariantCulture);
    }

    [SkippableFact]
    public void Host_FullWindow_GoldenEdgeToEdge()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint W = 256;
        const uint H = 128;

        // A blue panel filling the client, hosting a label in the built-in face. The window's Padding is zeroed, so
        // the Border reaches all four corners instead of leaving the theme's window background showing at the edges.
        var text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        text.Text("MewStage").Foreground(Color.FromArgb(255, 255, 255, 255));
        Window window = new Window { Padding = Thickness.Zero }.Content(
            new Border { Background = Color.FromArgb(255, 40, 90, 180) }.Child(text));
        using EmbeddedUiHost host = NewHost(dev, window, (int)W, (int)H);
        UiFrame.Image img = UiFrame.Host(dev, host, W, H);

        (int X, int Y, string Name)[] probes =
        [
            (0, 0, "top-left"), ((int)W - 1, 0, "top-right"),
            (0, (int)H - 1, "bottom-left"), ((int)W - 1, (int)H - 1, "bottom-right"),
            ((int)W / 2, 0, "top-mid"), (0, (int)H / 2, "left-mid"),
            ((int)W - 1, (int)H / 2, "right-mid"), ((int)W / 2, (int)H - 1, "bottom-mid"),
        ];
        foreach ((int x, int y, string name) in probes)
        {
            (int r, int g, int b) = img.Px(x, y);
            Assert.True(b > 90 && b > r + 20, S($"panel fill missing at {name} ({x},{y}): ({r},{g},{b}) - expected the blue Border, got the window clear"));
        }

        // The white glyphs sit on the blue panel somewhere (text went through the glyph-atlas path).
        int bright = 0;
        for (int y = 0; y < (int)H; y++)
        {
            for (int x = 0; x < (int)W; x++)
            {
                (int r, int g, _) = img.Px(x, y);
                if (r > 150 && g > 150)
                {
                    bright++;
                }
            }
        }

        Console.WriteLine(S($"[golden] white text pixels on the panel: {bright}"));
        Assert.True(bright > 40, S($"the text was not drawn ({bright} bright pixels)"));
    }

    [SkippableFact]
    public void Host_DesignSpace_BelowOne_DrawsFarCorner()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();

        // A red block in the bottom-right corner of the 1920 x 1080 design canvas, shown on a 1280 x 720 target.
        Border block = new Border { Background = Color.FromArgb(255, 230, 30, 30) }.Width(100).Height(60);
        Canvas canvas = new Canvas().Width(1920).Height(1080);
        canvas.Add(block.CanvasPosition(1800, 1000));
        const uint Wp = 1280;
        const uint Hp = 720;
        (byte[] vs, byte[] ps) = TestAssets.UiShaders();
        using var host = new EmbeddedUiHost(dev, vs, ps) { UseDesignSpace = true };
        host.Resize((int)Wp, (int)Hp, 1.0);
        host.SetWindows([Black(canvas)]);
        Assert.Equal(Wp / 1920.0, host.EffectiveScale, 9);

        UiFrame.Image img = UiFrame.Host(dev, host, Wp, Hp);
        (int R, int G, int B) corner = img.Px(1233, 686);   // design (1850, 1030) * 2/3
        (int R, int G, int B) left = img.Px(1150, 686);     // design x = 1725: left of the block
        Console.WriteLine(S($"[design-space] corner={corner} left={left}"));
        Assert.True(corner.R > 200 && corner.G < 60, S($"the far corner of the design canvas was clipped or culled: {corner}"));
        Assert.True(left.R < 30, S($"GATE: the block is drawn where it is not: {left}"));
    }

    [SkippableFact]
    public void Host_ComboBox_OpensInSurface()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint Wp = 320;
        const uint Hp = 240;
        ComboBox combo = new ComboBox().Items("ALPHA", "BRAVO", "CHARLIE", "DELTA").Width(160);
        Canvas canvas = new Canvas().Width(Wp).Height(Hp);
        canvas.Add(combo.CanvasPosition(20, 20));
        using EmbeddedUiHost host = NewHost(dev, Black(canvas), (int)Wp, (int)Hp);

        UiFrame.Image closed = UiFrame.Host(dev, host, Wp, Hp);
        combo.IsDropDownOpen = true;
        UiFrame.Image open = UiFrame.Host(dev, host, Wp, Hp);

        // The list opens below the header; count the samples that changed there.
        int changed = 0;
        for (int y = 70; y < 160; y += 2)
        {
            for (int x = 24; x < 176; x += 2)
            {
                if (open.Px(x, y) != closed.Px(x, y))
                {
                    changed++;
                }
            }
        }

        Console.WriteLine(S($"[combo] changed samples below the header: {changed}"));
        Assert.True(combo.IsDropDownOpen, "the drop-down closed itself (no surface to open into)");
        Assert.True(changed > 200, S($"the open list is not drawn in the surface ({changed} samples changed)"));
    }

    [SkippableFact]
    public void Host_Opacity_Half_CompositesAtHalf()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint Wp = 128;
        const uint Hp = 64;
        Border white = new Border { Background = Color.FromArgb(255, 255, 255, 255), Opacity = 0.5 }.Width(Wp).Height(Hp);
        Canvas canvas = new Canvas().Width(Wp).Height(Hp);
        canvas.Add(white.CanvasPosition(0, 0));
        using EmbeddedUiHost host = NewHost(dev, Black(canvas), (int)Wp, (int)Hp);
        UiFrame.Image img = UiFrame.Host(dev, host, Wp, Hp);
        (int R, int G, int B) mid = img.Px(64, 32);
        Console.WriteLine(S($"[opacity] mid={mid}"));

        // White at 0.5 over black: 127.5, rounded either way.
        Assert.True(Math.Abs(mid.R - 128) <= 2 && Math.Abs(mid.G - 128) <= 2 && Math.Abs(mid.B - 128) <= 2, S($"Opacity 0.5 did not composite at half strength: {mid}"));
    }

    [SkippableFact]
    public void Host_Resize_ReleasesKeptContext_AndDrawsTheNewSize()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        var fill = new Border { Background = Color.FromArgb(255, 30, 200, 60) };
        Window window = Black(fill);
        using EmbeddedUiHost host = NewHost(dev, window, 160, 90);
        UiFrame.Host(dev, host, 160, 90);
        Assert.NotNull(KeptContext(window));

        host.Resize(320, 180, 1.0);
        Assert.Null(KeptContext(window));                 // released before the old surface went
        UiFrame.Image img = UiFrame.Host(dev, host, 320, 180);
        Assert.NotNull(KeptContext(window));
        (int R, int G, int B) far = img.Px(315, 175);
        Console.WriteLine(S($"[resize] far corner after resize={far}"));
        Assert.True(far.G > 180 && far.R < 60, S($"the frame after the resize did not fill the new size: {far}"));
    }

    private static object? KeptContext(Window w)
    {
        return typeof(Window).GetField("_presentSurfaceContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w);
    }

    private static Window OpaqueMenu()
    {
        return new Window { Padding = Thickness.Zero }.Content(new Border { Background = Color.FromArgb(255, 40, 90, 180) });
    }

    private static Window TransparentModal(uint w, uint h)
    {
        // A transparent modal: AllowsTransparency + a transparent background, so the window clear is an alpha no-op;
        // a half-black scrim over the whole client and a centered opaque red card.
        Canvas canvas = new Canvas().Width(w).Height(h);
        canvas.Add(new Border { Background = Color.FromArgb(128, 0, 0, 0) }.Width(w).Height(h).CanvasPosition(0, 0));
        canvas.Add(new Border { Background = Color.FromArgb(255, 220, 40, 40) }.Width(80).Height(40).CanvasPosition((w - 80) / 2.0, (h - 40) / 2.0));
        return new Window { Padding = Thickness.Zero, AllowsTransparency = true, Background = Color.Transparent }.Content(canvas);
    }

    [SkippableFact]
    public void Host_WindowStack_CompositesBottomToTop()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint W = 256;
        const uint H = 128;
        (byte[] vs, byte[] ps) = TestAssets.UiShaders();
        using var host = new EmbeddedUiHost(dev, vs, ps);
        host.Resize((int)W, (int)H, 1.0);
        host.SetWindows([OpaqueMenu(), TransparentModal(W, H)]);   // bottom to top, one ui pass

        // The host clears to green when asked to: a compositing miss shows green.
        host.MenuBackground = new ClearColor(0f, 1f, 0f, 1f);
        UiFrame.Image img = UiFrame.Host(dev, host, W, H, clearBackground: true);

        (double R, double G, double B) corner = img.Mean(4, 4, 16, 16);                         // scrim over the menu
        (double R, double G, double B) center = img.Mean(((int)W / 2) - 8, ((int)H / 2) - 8, 16, 16);   // the card
        Console.WriteLine(S($"[composite] corner R={corner.R:F1} G={corner.G:F1} B={corner.B:F1}; center R={center.R:F1} G={center.G:F1} B={center.B:F1}"));

        // The opaque menu shows THROUGH the scrim (blue-dominant, darkened), not the green clear.
        Assert.True(corner.B > corner.R + 25 && corner.B > corner.G + 20, S($"the menu did not show through the modal scrim: {corner}"));
        Assert.True(corner.G < 120, S($"the green clear leaked through (compositing failed): G={corner.G:F1}"));
        Assert.True(corner.B < 170, S($"the scrim did not blend over the menu (too bright): B={corner.B:F1}"));
        Assert.True(center.R > center.B + 40 && center.R > 120, S($"the modal card is missing on top: {center}"));
    }

    [SkippableFact]
    public void Host_LoadNotClear_KeepsWorldPixels()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint W = 256;
        const uint H = 128;
        (byte[] vs, byte[] ps) = TestAssets.UiShaders();
        using var host = new EmbeddedUiHost(dev, vs, ps);
        host.Resize((int)W, (int)H, 1.0);
        host.SetWindows([TransparentModal(W, H)]);

        // A "world" pass fills the target green first; the ui pass LOADS it and composites the transparent window
        // over it, so the green survives under the scrim and the card sits on top.
        UiFrame.Image img = UiFrame.Host(dev, host, W, H, clearBackground: false, beforeUi: (graph, color) => graph.AddPass("world", PassKind.Graphics, s =>
            {
                s.Write(color, ResourceState.RenderTarget);
                s.SetExecute((in c) =>
                {
                    Span<ColorAttachment> ca = [new ColorAttachment { TextureHandle = c.Resolve(color), Clear = true, ClearValue = new ClearColor(0f, 1f, 0f, 1f) }];
                    var da = default(DepthAttachment);
                    c.Recorder.BeginRendering(ca, in da, W, H);
                    c.Recorder.EndRendering();
                });
            }));

        (double R, double G, double B) corner = img.Mean(4, 4, 16, 16);
        (double R, double G, double B) center = img.Mean(((int)W / 2) - 8, ((int)H / 2) - 8, 16, 16);
        Console.WriteLine(S($"[load] corner R={corner.R:F1} G={corner.G:F1} B={corner.B:F1}; center R={center.R:F1} G={center.G:F1} B={center.B:F1}"));
        Assert.True(corner.G > 90 && corner.G > corner.R + 40 && corner.G > corner.B + 40, S($"the world's pixels did not survive the ui pass (it cleared): {corner}"));
        Assert.True(corner.G < 170, S($"the scrim did not darken the world: G={corner.G:F1}"));
        Assert.True(center.R > center.G + 40 && center.R > 120, S($"the card is missing on top of the world: {center}"));
    }
}
