using System;
using System.Globalization;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Graphics;
using MewStage.Input;

using SDL;

using Xunit;

using static SDL.SDL3;

namespace MewStage.Host.Tests.Lavapipe;

/// <summary>
/// The whole host stack offscreen: SDL's offscreen video driver, a headless Vulkan device (lavapipe) and the offscreen
/// swapchain, driven by the real frame loop (pump, input, resize, `ui` pass, present). This is the windowed smoke
/// ("a label in a window") the hand test repeats on a real window with DX12 and Vulkan.
/// </summary>
[Collection(HostLavapipeGate.Collection)]
public sealed unsafe class AppHostOffscreenTests
{
    private static string S(FormattableString s)
    {
        return s.ToString(CultureInfo.InvariantCulture);
    }

    private static Window BluePanelWithText(string text)
    {
        var label = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        label.Text(text).Foreground(Color.FromArgb(255, 255, 255, 255));
        return new Window { Padding = Thickness.Zero }.Content(new Border { Background = Color.FromArgb(255, 40, 90, 180) }.Child(label));
    }

    [SkippableFact]
    public void AppHost_Offscreen_RendersALabelWindow_ThroughTheFrameLoop()
    {
        HostLavapipeGate.Require();
        using var host = new AppHost(GpuBackend.Vulkan, "MewStage smoke", 320, 180, null, 0, 0, offscreen: true);
        using var rb = new BackbufferReadback(host.Gpu.Device);
        host.BeforePresent = rb.Record;
        host.Ui.SetWindow(BluePanelWithText("MewStage"));
        Assert.NotNull(host.Ui.Host);
        Assert.True(host.Gpu.IsOffscreen);
        Assert.Equal(1, host.WindowCount);

        for (int i = 0; i < 3; i++)
        {
            Assert.True(host.RunFrame());
        }

        host.Gpu.WaitIdle();
        Assert.Equal(3, host.FrameCount);
        (int R, int G, int B) corner = rb.Px(2, 2);
        (int R, int G, int B) far = rb.Px(317, 177);
        int white = rb.Count(p => p.R > 150 && p.G > 150 && p.B > 150);
        Console.WriteLine(S($"[smoke] corner={corner} far={far} whiteText={white}"));
        Assert.True(corner.B > 120 && corner.B > corner.R + 40, S($"the panel did not reach the top-left corner: {corner}"));
        Assert.True(far.B > 120 && far.B > far.R + 40, S($"the panel did not reach the bottom-right corner: {far}"));
        Assert.True(white > 30, S($"the label text was not drawn ({white} white pixels)"));
    }

    [SkippableFact]
    public void AppHost_Offscreen_PumpRoutesPushedEvents_AndCloseEndsTheLoop()
    {
        HostLavapipeGate.Require();
        using var host = new AppHost(GpuBackend.Vulkan, "MewStage input", 320, 180, null, 0, 0, offscreen: true);
        host.Ui.SetWindow(BluePanelWithText("input"));
        uint windowId = host.Gpu.WindowId;
        Assert.NotEqual(0u, windowId);

        // A key press and a pointer move pushed into SDL's queue reach the snapshot through the real pump.
        SDL_Event key = default;
        key.type = (uint)SDL_EventType.SDL_EVENT_KEY_DOWN;
        key.key.windowID = (SDL_WindowID)windowId;
        key.key.scancode = SDL_Scancode.SDL_SCANCODE_SPACE;
        key.key.down = true;
        Assert.True(SDL_PushEvent(&key));
        SDL_Event move = default;
        move.type = (uint)SDL_EventType.SDL_EVENT_MOUSE_MOTION;
        move.motion.windowID = (SDL_WindowID)windowId;
        move.motion.x = 100f;
        move.motion.y = 60f;
        Assert.True(SDL_PushEvent(&move));
        Assert.True(host.RunFrame());
        Assert.True(host.Input.IsKeyDown(KeyCode.Space));
        Assert.Equal((100f, 60f), (host.Input.MouseX, host.Input.MouseY));

        // The OS close button (a close request for the main window) ends the loop.
        SDL_Event close = default;
        close.type = (uint)SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED;
        close.window.windowID = (SDL_WindowID)windowId;
        Assert.True(SDL_PushEvent(&close));
        Assert.False(host.RunFrame());
        Assert.True(host.Input.CloseRequested);
    }

    [SkippableFact]
    public void AppHost_Offscreen_OverlayLoadsTheWorld_UnderATransparentWindow()
    {
        HostLavapipeGate.Require();
        using var host = new AppHost(GpuBackend.Vulkan, "MewStage overlay", 256, 128, null, 0, 0, offscreen: true);
        using var rb = new BackbufferReadback(host.Gpu.Device);
        host.BeforePresent = rb.Record;
        host.UiClearsBackground = false;

        // The "world": a pass that clears the backbuffer green. The UI then LOADS it.
        host.BeforeUi = (graph, bb) => graph.AddPass("world", PassKind.Graphics, s =>
        {
            s.Write(bb, ResourceState.RenderTarget);
            s.SetExecute((in c) =>
            {
                Span<ColorAttachment> ca = [new ColorAttachment { TextureHandle = c.Resolve(bb), Clear = true, ClearValue = new ClearColor(0f, 1f, 0f, 1f) }];
                var da = default(DepthAttachment);
                c.Recorder.BeginRendering(ca, in da, 256, 128);
                c.Recorder.EndRendering();
            });
        });

        Canvas canvas = new Canvas().Width(256).Height(128);
        canvas.Add(new Border { Background = Color.FromArgb(255, 220, 40, 40) }.Width(80).Height(40).CanvasPosition(88, 44));
        host.Ui.SetWindow(new Window { Padding = Thickness.Zero, AllowsTransparency = true, Background = Color.Transparent }.Content(canvas));
        for (int i = 0; i < 2; i++)
        {
            Assert.True(host.RunFrame());
        }

        host.Gpu.WaitIdle();
        (double R, double G, double B) corner = rb.Mean(4, 4, 16, 16);
        (double R, double G, double B) card = rb.Mean(120, 56, 16, 16);
        Console.WriteLine(S($"[overlay] corner G={corner.G:F1} R={corner.R:F1}; card R={card.R:F1} G={card.G:F1}"));
        Assert.True(corner.G > 200 && corner.R < 40, S($"the world's green did not survive the ui pass: {corner}"));
        Assert.True(card.R > 180 && card.G < 80, S($"the HUD card is missing over the world: {card}"));
    }

    [SkippableFact]
    public void AppHost_Offscreen_SecondaryWindow_OpensRendersAndCloses()
    {
        HostLavapipeGate.Require();
        using var host = new AppHost(GpuBackend.Vulkan, "MewStage main", 320, 180, null, 0, 0, offscreen: true);
        host.Ui.SetWindow(BluePanelWithText("main"));
        Assert.True(host.RunFrame());

        SecondaryWindow second = host.OpenSecondary("MewStage second", 40, 40, 200, 120);
        using var rb = new BackbufferReadback(host.Gpu.Device);
        second.BeforePresent = rb.Record;
        second.Show(new Window { Padding = Thickness.Zero }.Content(new Border { Background = Color.FromArgb(255, 200, 40, 40) }));
        Assert.Equal(2, host.WindowCount);
        Assert.NotEqual(host.Gpu.WindowId, second.WindowId);
        Assert.Equal(host.Gpu.WindowId, second.OwnerWindowId);
        Assert.NotNull(second.Ui.Host);
        Assert.Same(host.Ui.Host!.Factory, second.Ui.Host.Factory);   // the shared graphics factory (fonts, text engine)

        for (int i = 0; i < 2; i++)
        {
            Assert.True(host.RunFrame());
        }

        host.Gpu.WaitIdle();
        Assert.Equal(2, second.FramesPresented);
        Assert.Equal((200u, 120u), (rb.Width, rb.Height));
        (int R, int G, int B) mid = rb.Px(100, 60);
        Console.WriteLine(S($"[secondary] mid={mid}"));
        Assert.True(mid.R > 150 && mid.G < 80, S($"the secondary window's content was not drawn: {mid}"));

        // Its own close button routes to it, not to the main window; the application closes it at the frame boundary.
        SDL_Event close = default;
        close.type = (uint)SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED;
        close.window.windowID = (SDL_WindowID)second.WindowId;
        Assert.True(SDL_PushEvent(&close));
        Assert.True(host.RunFrame());
        Assert.True(second.CloseRequested);
        Assert.False(host.Input.CloseRequested);
        host.CloseSecondary(second);
        Assert.False(second.IsOpen);
        Assert.Equal(1, host.WindowCount);
        Assert.True(host.RunFrame());   // the main window carries on
    }

    [SkippableFact]
    public void AppHost_Offscreen_DisplayScaleOverride_LaysOutInDip()
    {
        HostLavapipeGate.Require();
        GpuWindow.OffscreenDisplayScaleOverride = 1.25f;
        try
        {
            using var host = new AppHost(GpuBackend.Vulkan, "MewStage dpi", 400, 250, null, 0, 0, offscreen: true);
            host.Ui.SetWindow(BluePanelWithText("dpi"));
            Assert.True(host.RunFrame());
            Assert.Equal(1.25f, host.Gpu.DisplayScale);
            Assert.Equal(1.25f, host.Ui.Bridge.Scale);
            Assert.Equal(new Size(320, 200), host.Ui.TopWindow!.ClientSize);   // 400 x 250 px at 125 % = 320 x 200 DIP
        }
        finally
        {
            GpuWindow.OffscreenDisplayScaleOverride = null;
        }
    }
}
