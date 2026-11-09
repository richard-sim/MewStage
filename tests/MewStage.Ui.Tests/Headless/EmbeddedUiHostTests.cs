using System;
using System.Diagnostics;
using System.Reflection;

using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;

using MewStage.Rendering.Backends.Headless;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Headless;

/// <summary>
/// The embedded host's contract on the headless device: backends attach and Loaded fires once the window is sized,
/// windows that leave the stack are pruned and their kept context released, only the top window is active, a resize
/// releases every kept context, OnRequest renders only on demand (and allocates nothing while idle), Continuous
/// renders every frame without a Gen2, the host's pulse drives a style Transition deterministically, and the
/// dispatcher pump enables a command-bound button once its handler is registered.
/// </summary>
[Collection(IdleHostCollection.Name)]
public sealed class EmbeddedUiHostTests
{
    public EmbeddedUiHostTests()
    {
        IdleHostCollection.SettleAnimations();
    }

    private static Window Screen(string title)
    {
        return new Window().Content(new StackPanel().Children(new Label().Text(title), new Button().Content(new Label().Text("ok"))));
    }

    private static object? KeptContext(Window w)
    {
        return typeof(Window).GetField("_presentSurfaceContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w);
    }

    [Fact]
    public void Host_AttachesBackend_AndRaisesLoaded_OnceSized()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        Window w = Screen("a");
        int loaded = 0;
        w.Loaded += () => loaded++;

        host.SetWindows([w]);
        Assert.NotEqual(0, (int)w.Handle);              // a backend is attached (PerformLayout needs Handle != 0)
        Assert.Equal(0, loaded);                        // not sized yet: Loaded waits

        host.Resize(320, 200, 1.0);
        Assert.Equal(1, loaded);                        // attached + sized: raised exactly once
        host.Resize(640, 400, 1.0);
        Assert.Equal(1, loaded);                        // a later resize does not raise it again
    }

    [Fact]
    public void Host_PrunesBackend_WhenWindowLeavesStack_AndReleasesContext()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        Window bottom = Screen("bottom");
        Window top = Screen("top");
        host.Resize(320, 200, 1.0);
        host.SetWindows([bottom, top]);
        var cmd = new HeadlessGraphicsList();
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
        Assert.NotNull(KeptContext(bottom));
        Assert.NotNull(KeptContext(top));
        Assert.True(cmd.Draws > 0, "the two windows drew nothing");

        host.SetWindows([bottom]);                     // the top window left the stack
        Assert.Null(KeptContext(top));                  // its kept context was released
        Assert.NotNull(KeptContext(bottom));            // the survivor keeps its context
        Assert.False(top.IsActive);
        Assert.Same(bottom, host.TopWindow);
    }

    [Fact]
    public void Host_OnlyTopWindow_IsActive()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        Window a = Screen("a");
        Window b = Screen("b");
        host.Resize(320, 200, 1.0);
        host.SetWindows([a, b]);
        Assert.False(a.IsActive);
        Assert.True(b.IsActive);

        host.SetWindows([b, a]);                        // reorder: the new top takes over
        Assert.True(a.IsActive);
        Assert.False(b.IsActive);
    }

    [Fact]
    public void Host_Resize_ReleasesKeptContexts_AndRelayouts()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        Window w = Screen("a");
        host.Resize(320, 200, 1.0);
        host.SetWindows([w]);
        var cmd = new HeadlessGraphicsList();
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd));
        Assert.NotNull(KeptContext(w));
        Assert.False(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd));   // idle

        host.Resize(640, 400, 1.0);
        Assert.Null(KeptContext(w));                    // released before the old surface went
        Assert.Equal(640, w.ClientSize.Width, 3);       // metrics re-applied in DIPs (scale 1)
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd));   // the resize invalidated
        Assert.NotNull(KeptContext(w));
    }

    [Fact]
    public void Host_DesignSpace_ScalesTheLayout_NotTheWindowDips()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader) { UseDesignSpace = true };
        Window w = Screen("hud");
        host.SetWindows([w]);
        host.Resize(1280, 720, 1.0);
        Assert.Equal(1280.0 / 1920.0, host.EffectiveScale, 9);
        Assert.Equal(1920, w.ClientSize.Width, 3);      // laid out in the design space
        Assert.Equal(1080, w.ClientSize.Height, 3);
        Assert.Equal(64u, w.Dpi);                       // 96 * 2/3, so DIP snapping lands on the surface's pixel grid
    }

    [Fact]
    public void Host_OnRequest_Idle_AddsNoPass_AllocatesZero()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(400, 300, 1.0);
        host.SetWindows([Screen("menu")]);
        var cmd = new HeadlessGraphicsList();

        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd));   // the first frame paints
        var idle = new Rendering.Graph.RenderGraph();
        for (int i = 0; i < 3; i++)
        {
            Assert.False(host.TickAndRender(idle, HeadlessUiFrame.Target, UiInvalidationMode.OnRequest));
        }

        int gen2Before = GC.CollectionCount(2);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 240; i++)
        {
            Assert.False(host.TickAndRender(idle, HeadlessUiFrame.Target, UiInvalidationMode.OnRequest));
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(gen2Before, GC.CollectionCount(2));
        Assert.Equal(0, allocated);
        Assert.Equal(1, host.RenderCount);
    }

    [Fact]
    public void Host_OnRequest_OneInvalidation_RendersExactlyOnce()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(320, 200, 1.0);
        host.SetWindows([Screen("a")]);
        var cmd = new HeadlessGraphicsList();
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd);
        Assert.Equal(1, host.RenderCount);

        host.Invalidate();
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd));
        Assert.Equal(2, host.RenderCount);
        Assert.False(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd));
        Assert.Equal(2, host.RenderCount);
    }

    [Fact]
    public void Host_Continuous_RendersEveryFrame_NoGen2()
    {
        using var dev = new HeadlessDevice();
        var speed = new ObservableValue<string>("0");
        Window window = new Window().Content(new StackPanel().Children(new Label().BindText(speed), new Label().Text("HUD")));
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(800, 480, 1.0);
        host.SetWindows([window]);
        var cmd = new HeadlessGraphicsList();
        for (int i = 0; i < 5; i++)
        {
            speed.Value = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
        }

        int gen2Before = GC.CollectionCount(2);
        for (int i = 0; i < 300; i++)
        {
            speed.Value = i.ToString(System.Globalization.CultureInfo.InvariantCulture);   // a per-frame readout, the retained-render point
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
        }

        Assert.Equal(gen2Before, GC.CollectionCount(2));
        Assert.Equal(305, host.RenderCount);
    }

    [Fact]
    public void Host_Pulse_AdvancesTransition_Deterministically()
    {
        using var dev = new HeadlessDevice();
        var from = Color.FromArgb(255, 200, 0, 0);
        var to = Color.FromArgb(255, 0, 0, 200);
        const int DurationMs = 200;
        var sheet = new StyleSheet();
        sheet.Define<Border>(Style.DeriveFromDefault<Border>(
            setters: [Setter.Create(Control.BackgroundProperty, from)],
            triggers: [new StateTrigger { Exclude = VisualStateFlags.Enabled, Setters = [Setter.Create(Control.BackgroundProperty, to)] }],
            transitions: [Transition.Create(Control.BackgroundProperty, DurationMs, Easing.Linear)]));
        Border border = new Border().Width(100).Height(50);
        Canvas panel = new Canvas { StyleSheet = sheet }.Width(320).Height(200);
        panel.Add(border.CanvasPosition(10, 10));
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(320, 200, 1.0);
        host.SetWindows([new Window().Content(panel)]);
        var cmd = new HeadlessGraphicsList();

        long t0 = Stopwatch.GetTimestamp();
        long ms = Stopwatch.Frequency / 1000;
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd, timestamp: t0);
        Assert.Equal(from, border.Background);

        border.IsEnabled = false;   // the trigger now asks for `to`, through the transition

        // Frame 1 resolves the new visual state (in layout, after that frame's pulse) and starts the transition's clock;
        // the clock's run begins at the NEXT pulse.
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd, timestamp: t0 + (1 * ms));
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd, timestamp: t0 + (2 * ms));
        Assert.Equal(from, border.Background);
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd, timestamp: t0 + ((2 + (DurationMs / 2)) * ms));
        Color half = border.Background;
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd, timestamp: t0 + ((2 + (2 * DurationMs)) * ms));
        Color done = border.Background;
        Assert.True(half.R is > 60 and < 140 && half.B is > 60 and < 140, $"halfway through the pulse-driven transition the color is {half}, not midway");
        Assert.Equal(to, done);
    }

    [Fact]
    public void Host_PumpDispatcher_EnablesCommandBoundButton_AfterHandlerRegistered()
    {
        using var dev = new HeadlessDevice();
        var command = new Command("test.go");
        Button button = new Button { Command = command }.Content(new Label().Text("Go"));
        StackPanel root = new StackPanel().Children(button);
        Window window = new Window().Content(root);
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(320, 200, 1.0);
        host.SetWindows([window]);
        var cmd = new HeadlessGraphicsList();
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd);
        Assert.False(button.IsEffectivelyEnabled);                 // no handler anywhere: the bound button is grayed out

        root.Commands.Register(command, () => { });
        Assert.False(button.IsEffectivelyEnabled);                 // registering alone changes nothing until the pump runs
        host.PumpDispatcher();
        Assert.True(button.IsEnabled);
    }

    [Fact]
    public void Host_WantsInput_WhenTheTopWindowHasAModalDisable()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        Window w = Screen("a");
        host.Resize(320, 200, 1.0);
        host.SetWindows([w]);
        Assert.False(host.WantsInput);
        w.AcquireModalDisable();                        // what a modal child does to its owner (internal: IVT grant)
        Assert.True(host.WantsInput);
        w.ReleaseModalDisable();
        Assert.False(host.WantsInput);
    }
}
