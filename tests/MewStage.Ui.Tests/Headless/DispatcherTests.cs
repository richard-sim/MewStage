using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Tasks;

using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Controls;

using MewStage.Rendering.Backends.Headless;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Headless;

/// <summary>
/// The thread's dispatcher (vendor patch multi-host/0003) and the frame's pulse: one dispatcher per UI thread, shared
/// by its hosts and unbound by the last; DispatcherTimer ticks on the frame clock; posted work runs at the next pump; a
/// focused TextBox's caret blink makes an idle OnRequest host render; an application-wide clock renders through the
/// pulse until it completes; pseudo-handles stay unique across threads.
/// </summary>
[Collection(IdleHostCollection.Name)]
public sealed class DispatcherTests
{
    private static readonly long Ms = Stopwatch.Frequency / 1000;

    public DispatcherTests()
    {
        IdleHostCollection.SettleAnimations();
    }

    [Fact]
    public void Dispatcher_IsBoundPerThread_SharedByItsHosts_AndUnboundByTheLast()
    {
        using var dev = new HeadlessDevice();
        var first = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        IDispatcher? bound = Application.ResolveDispatcher();
        Assert.IsType<EmbeddedDispatcher>(bound);
        Assert.Same(EmbeddedDispatcher.ForThread, bound);

        var second = new EmbeddedUiHost(dev, first.Factory, TestAssets.DummyShader, TestAssets.DummyShader);
        Assert.Same(bound, Application.ResolveDispatcher());       // a second host on the thread shares it
        Assert.Equal(2, EmbeddedDispatcher.ForThread!.HostCount);

        first.Dispose();
        Assert.Same(bound, Application.ResolveDispatcher());       // still held by the second host
        second.Dispose();
        Assert.Null(Application.ResolveDispatcher());               // the last host unbound it
        Assert.Null(EmbeddedDispatcher.ForThread);
    }

    [Fact]
    public void DispatcherTimer_TicksOnTheFrameClock()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(320, 200, 1.0);
        host.SetWindows([new Window().Content(new Label().Text("timer"))]);
        var cmd = new HeadlessGraphicsList();
        long t0 = Stopwatch.GetTimestamp();
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0);

        int ticks = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(500));
        timer.Tick += () => ticks++;
        timer.Start();                                              // due at the frame clock + 500 ms

        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0 + (400 * Ms));
        Assert.Equal(0, ticks);
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0 + (600 * Ms));
        Assert.Equal(1, ticks);
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0 + (1150 * Ms));
        Assert.Equal(2, ticks);
        timer.Stop();
    }

    [Fact]
    public void PostedWork_RunsAtTheNextPump()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        int ran = 0;
        Application.ResolveDispatcher()!.BeginInvoke(() => ran++);
        Assert.Equal(0, ran);
        host.PumpDispatcher();
        Assert.Equal(1, ran);
    }

    [Fact]
    public void FocusedTextBox_CaretBlink_RendersAnIdleOnRequestHost()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        TextBox box = new TextBox().Text("caret");
        Window window = new Window().Content(new StackPanel().Children(box));
        host.Resize(320, 200, 1.0);
        host.SetWindows([window]);
        var cmd = new HeadlessGraphicsList();
        long t0 = Stopwatch.GetTimestamp();
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0));
        window.FocusManager.SetFocus(box);                          // starts the caret timer (500 ms)
        long t = t0;
        for (int i = 0; i < 25; i++)                                 // the focus visuals settle well inside 400 ms
        {
            t += 16 * Ms;
            HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t);
        }

        Assert.False(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t + (16 * Ms)), "the focused box still renders before its first blink");
        int before = host.RenderCount;
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0 + (600 * Ms)), "the caret blink did not reach the host");
        Assert.Equal(before + 1, host.RenderCount);
        window.FocusManager.ClearFocus();
    }

    [Fact]
    public void ApplicationWideClock_RendersThroughThePulse_UntilItCompletes()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(320, 200, 1.0);
        host.SetWindows([new Window().Content(new Label().Text("pulse"))]);
        var cmd = new HeadlessGraphicsList();
        long t0 = Stopwatch.GetTimestamp();
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0));
        Assert.False(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0 + (16 * Ms)));

        var clock = new AnimationClock(TimeSpan.FromMilliseconds(200));   // no element owns it: application-wide demand
        clock.Start();
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0 + (32 * Ms)));
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0 + (1000 * Ms)));   // completes; its last tick still renders
        Assert.False(clock.IsRunning);
        Assert.False(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.OnRequest, cmd, timestamp: t0 + (1016 * Ms)));
    }

    [Fact]
    public void PseudoHandles_AreUniqueAcrossThreads()
    {
        var handles = new ConcurrentDictionary<nint, byte>();
        Parallel.For(0, 4000, _ =>
        {
            nint handle = new EmbeddedWindowBackend().Handle;
            Assert.NotEqual(0, (long)handle);
            Assert.True(handles.TryAdd(handle, 0), "two backends got the same pseudo-handle");
        });
        Assert.Equal(4000, handles.Count);
    }
}
