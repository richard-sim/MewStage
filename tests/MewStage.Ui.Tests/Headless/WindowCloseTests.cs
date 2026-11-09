using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Rendering.Backends.Headless;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Headless;

/// <summary>
/// Window.Close() on the embedded host, which does what an OS backend's destroy does: Closing is asked (a handler can
/// cancel), Closed is raised, the window leaves the stack and the one under it becomes active, the visual tree is
/// disposed so a cached Image hands its scratch surface back to the pool (a later identical Image reuses it, a trim
/// frees it), a closed window cannot be shown again, and a window already pruned from the stack still closes.
/// </summary>
[Collection(IdleHostCollection.Name)]
public sealed class WindowCloseTests
{
    public WindowCloseTests()
    {
        IdleHostCollection.SettleAnimations();
    }

    private static Window Screen(string title)
    {
        return new Window().Content(new StackPanel().Children(new Label().Text(title)));
    }

    private static (Window Window, TestVectorSource Source) IconWindow(byte green)
    {
        var source = new TestVectorSource(24, 24, Color.FromArgb(255, 0, green, 0));
        var image = new Image { Source = source };   // default mode: Cached
        image.Width(48).Height(48);
        return (new Window { Padding = Thickness.Zero }.Content(image), source);
    }

    [Fact]
    public void Close_RaisesClosed_LeavesTheStack_AndTheWindowUnderItBecomesActive()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(320, 200, 1.0);
        Window under = Screen("under");
        Window top = Screen("top");
        host.SetWindows([under, top]);
        Assert.Same(top, host.TopWindow);
        Assert.False(under.IsActive);

        int closing = 0, closed = 0;
        top.Closing += _ => closing++;
        top.Closed += () => closed++;
        top.Close();

        Assert.Equal((1, 1), (closing, closed));
        Assert.Same(under, host.TopWindow);
        Assert.True(under.IsActive);
        Assert.Equal(0, (int)top.Handle);   // the backend reports the window gone

        // A second Close (and a host close of a window it no longer holds) does nothing.
        top.Close();
        Assert.False(host.CloseWindow(top));
        Assert.Equal((1, 1), (closing, closed));

        var cmd = new HeadlessGraphicsList();
        Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));   // the stack below still renders
    }

    [Fact]
    public void ClosingHandlerThatCancels_KeepsTheWindow()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(320, 200, 1.0);
        Window w = Screen("keep");
        host.SetWindows([w]);
        int closed = 0;
        w.Closing += e => e.Cancel = true;
        w.Closed += () => closed++;

        w.Close();
        Assert.False(host.CloseWindow(w));

        Assert.Equal(0, closed);
        Assert.Same(w, host.TopWindow);
        Assert.NotEqual(0, (int)w.Handle);
        host.SetWindows([w]);   // still showable
    }

    [Fact]
    public void ClosedWindow_CannotBeShownAgain()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(320, 200, 1.0);
        Window w = Screen("gone");
        host.SetWindows([w]);
        Assert.True(host.CloseWindow(w));
        Assert.Null(host.TopWindow);
        _ = Assert.Throws<InvalidOperationException>(() => host.SetWindows([w]));
    }

    [Fact]
    public void Close_OnAWindowPrunedFromTheStack_StillCloses()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(320, 200, 1.0);
        Window hud = Screen("hud");
        Window menu = Screen("menu");
        host.SetWindows([hud, menu]);
        host.SetWindows([hud]);   // the menu popped: pruned, not closed (a pruned window can be hosted again)
        Assert.NotEqual(0, (int)menu.Handle);

        int closed = 0;
        menu.Closed += () => closed++;
        menu.Close();   // e.g. on the way out of the screen

        Assert.Equal(1, closed);
        Assert.Same(hud, host.TopWindow);   // the stack is untouched
        Assert.True(hud.IsActive);
        _ = Assert.Throws<InvalidOperationException>(() => host.SetWindows([hud, menu]));
    }

    [Fact]
    public void Close_ReturnsTheIconCacheToThePool_ASecondIdenticalImageReusesIt_AndATrimFreesIt()
    {
        using var dev = new HeadlessDevice();
        int registered = ExternalTextures.Count;
        var cmd = new HeadlessGraphicsList();
        using (var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader))
        {
            host.Resize(200, 120, 1.0);
            (Window first, TestVectorSource firstSource) = IconWindow(200);
            host.SetWindows([first]);
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
            Assert.Equal(1, firstSource.Renders);
            Assert.Equal(1, host.LiveScratchSurfaces);
            Assert.Equal(registered + 1, ExternalTextures.Count);

            // Closing hands the surface back to the pool: still alive, still registered, nothing leased.
            first.Close();
            Assert.Null(host.TopWindow);
            Assert.Equal(1, host.LiveScratchSurfaces);
            Assert.Equal(registered + 1, ExternalTextures.Count);

            // An identical Image in a new window rents the pooled surface instead of allocating another one.
            (Window second, TestVectorSource secondSource) = IconWindow(120);
            host.SetWindows([second]);
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
            Assert.Equal(1, secondSource.Renders);   // its own content, rasterized once into the reused surface
            Assert.Equal(1, host.LastFrameScratchRenders);
            Assert.Equal(1, host.LiveScratchSurfaces);
            Assert.Equal(registered + 1, ExternalTextures.Count);

            // A trim keeps what a shown window leases ...
            host.TrimCaches();
            Assert.Equal(1, host.LiveScratchSurfaces);

            // ... and frees what a closed one returned.
            second.Close();
            host.TrimCaches();
            Assert.Equal(0, host.LiveScratchSurfaces);
            Assert.Equal(registered, ExternalTextures.Count);
        }

        Assert.Equal(registered, ExternalTextures.Count);
    }
}
