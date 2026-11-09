using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

using MewStage.Rendering.Backends.Headless;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Headless;

/// <summary>
/// The scratch surfaces MewUI's caches render into, on the headless device: a cache descriptor gets a scratch surface
/// whose image view is a zero-copy external texture; a vector Image rasterizes once into it inside the ui pass (its
/// own render pass and barriers recorded ahead of the host's) and blits on the frames after; a source change and a
/// size change rasterize again; the frame refuses a scratch render outside the ui pass and the factory refuses
/// off-thread rendering, which is how MewUI learns to stay synchronous here.
/// </summary>
[Collection(IdleHostCollection.Name)]
public sealed class ScratchSurfaceTests
{
    public ScratchSurfaceTests()
    {
        IdleHostCollection.SettleAnimations();
    }

    private static (EmbeddedUiHost Host, HeadlessGraphicsList Cmd) NewHost(HeadlessDevice dev, Window window, int w, int h)
    {
        var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(w, h, 1.0);
        host.SetWindows([window]);
        return (host, new HeadlessGraphicsList());
    }

    [Fact]
    public void CacheDescriptor_GetsAScratchSurface_WithAZeroCopyImageView()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        int registered = ExternalTextures.Count;
        IRenderSurface surface = host.Factory.AcquireScratchSurface(37, 21, debugName: "test");
        Assert.True(surface.PixelWidth >= 37 && surface.PixelHeight >= 21);   // pool-fitted, at least the content
        Assert.NotEqual(SurfaceCapabilities.None, surface.Capabilities & SurfaceCapabilities.GpuSampleable);
        Assert.NotEqual(SurfaceCapabilities.None, surface.Capabilities & SurfaceCapabilities.Alpha);
        using IImage image = host.Factory.CreateImageView(surface);
        Assert.Equal((surface.PixelWidth, surface.PixelHeight), (image.PixelWidth, image.PixelHeight));
        Assert.Equal(registered + 1, ExternalTextures.Count);                // the color texture is registered, not copied
        host.Factory.ReleaseScratchSurface(surface);

        // A plain window surface is still a window surface.
        using IRenderSurface window = host.Factory.CreateSurface(new RenderSurfaceDescriptor(64, 64, 1.0, RenderPixelFormat.Bgra8888Premultiplied, SurfaceUsage.Offscreen, SurfaceCapabilities.Renderable));
        Assert.IsType<UiRenderSurface>(window);
    }

    [Fact]
    public void ScratchRender_OutsideTheUiPass_Throws_AndBackgroundScopeIsRefused()
    {
        using var dev = new HeadlessDevice();
        using var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        IRenderSurface surface = host.Factory.AcquireScratchSurface(16, 16);
        using IGraphicsContext ctx = host.Factory.CreateContext(surface);
        Assert.Throws<InvalidOperationException>(() => ctx.BeginFrame(surface));
        Assert.Throws<NotSupportedException>(host.Factory.AcquireBackgroundRenderScope);
        host.Factory.ReleaseScratchSurface(surface);
    }

    [Fact]
    public void VectorImage_RasterizesOnceIntoTheCache_ThenBlits_AndRerasterizesOnChange()
    {
        using var dev = new HeadlessDevice();
        var source = new TestVectorSource(40, 30, Color.FromArgb(255, 255, 0, 0));
        var image = new Image { Source = source };
        image.Width(80).Height(60);
        Window window = new Window { Padding = Thickness.Zero }.Content(image);
        int registered = ExternalTextures.Count;
        (EmbeddedUiHost host, HeadlessGraphicsList cmd) = NewHost(dev, window, 200, 120);
        using (host)
        {
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
            Assert.Equal(1, source.Renders);                       // rasterized into the cache
            Assert.Equal(registered + 1, ExternalTextures.Count);  // the cache's color texture, registered zero-copy
            Assert.Equal(1, host.LastFrameScratchRenders);
            Assert.Equal(1, host.Factory.Device.TotalScratchRenders);
            Assert.True(cmd.Barriers >= 3, "the scratch render records its own barriers (color to RenderTarget, depth, color to ShaderResource)");

            // A frame later: the bitmap is blitted, nothing rasterizes.
            cmd.Reset();
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
            Assert.Equal(1, source.Renders);
            Assert.Equal(0, host.LastFrameScratchRenders);

            // A new source rasterizes again, once.
            var second = new TestVectorSource(40, 30, Color.FromArgb(255, 0, 255, 0));
            image.Source = second;
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
            Assert.Equal(1, second.Renders);
            Assert.Equal(1, host.LastFrameScratchRenders);

            // Direct mode draws the vector on every repaint of the control (retained rendering: an invalidation, not
            // every frame) and holds no cache.
            image.VectorCacheMode = ImageVectorCacheMode.Direct;
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
            Assert.Equal(2, second.Renders);
            Assert.Equal(0, host.LastFrameScratchRenders);
            image.InvalidateVisual();
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
            Assert.Equal(3, second.Renders);
            Assert.Equal(0, host.LastFrameScratchRenders);
        }

        // The window was never closed, so the Image still holds its cache lease: the host's device disposed the
        // scratch surface anyway (its textures are the device's to free before the application's device goes).
        Assert.Equal(registered, ExternalTextures.Count);
    }

    [Fact]
    public void BitmapCache_RendersTheSubtreeIntoAScratchSurface_Once()
    {
        using var dev = new HeadlessDevice();
        int renders = 0;
        var probe = new TestVectorSource(10, 10, Color.FromArgb(255, 0, 0, 255));
        var cached = new Border
        {
            Background = Color.FromArgb(255, 40, 40, 40),
            CacheMode = new BitmapCache(),
            Child = new Image { Source = probe, VectorCacheMode = ImageVectorCacheMode.Direct }.Width(20).Height(20),
        };
        cached.Width(60).Height(60);
        Window window = new Window { Padding = Thickness.Zero }.Content(cached);
        (EmbeddedUiHost host, HeadlessGraphicsList cmd) = NewHost(dev, window, 120, 120);
        using (host)
        {
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
            renders = probe.Renders;
            Assert.True(renders >= 1);
            Assert.True(host.LastFrameScratchRenders >= 1, "the cached subtree did not render into a scratch surface");

            // The cache holds: the subtree (and the direct-mode image inside it) does not render again.
            Assert.True(HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, cmd));
            Assert.Equal(renders, probe.Renders);
            Assert.Equal(0, host.LastFrameScratchRenders);
        }
    }
}
