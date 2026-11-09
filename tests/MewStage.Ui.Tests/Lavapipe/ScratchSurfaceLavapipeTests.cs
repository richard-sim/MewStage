using System;
using System.Globalization;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Rendering.Backends.Vulkan;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Lavapipe;

/// <summary>
/// The scratch surfaces with pixels: a vector image rasterized into a cache inside the ui pass shows up in the window
/// exactly where a direct draw would put it, with its transparent surround compositing over the window background;
/// a bitmap-cached subtree draws the same as an uncached one; and two caches rasterized in one frame both survive
/// (the scratch renderer's per-flush buffers: a ring buffer would have the second overwrite the first).
/// </summary>
[Collection(UiLavapipeGate.Collection)]
public sealed class ScratchSurfaceLavapipeTests
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

    private static int Count(UiFrame.Image img, Func<(int R, int G, int B), bool> pred)
    {
        int n = 0;
        for (int y = 0; y < (int)img.Height; y++)
        {
            for (int x = 0; x < (int)img.Width; x++)
            {
                if (pred(img.Px(x, y)))
                {
                    n++;
                }
            }
        }

        return n;
    }

    [SkippableFact]
    public void VectorImage_CachedAndDirect_DrawTheSamePixels()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint W = 160;
        const uint H = 120;
        var red = Color.FromArgb(255, 220, 30, 30);

        // The vector is 40 x 30 drawn Uniform into an 80 x 60 control at (20, 20): the painted region is the whole control.
        static Canvas Scene(TestVectorSource source, ImageVectorCacheMode mode)
        {
            Canvas canvas = new Canvas().Width(160).Height(120);
            canvas.Add(new Image { Source = source, VectorCacheMode = mode }.Width(80).Height(60).CanvasPosition(20, 20));
            return canvas;
        }

        var cachedSource = new TestVectorSource(40, 30, red);
        using EmbeddedUiHost cachedHost = NewHost(dev, Black(Scene(cachedSource, ImageVectorCacheMode.Cached)), (int)W, (int)H);
        UiFrame.Image cached = UiFrame.Host(dev, cachedHost, W, H);
        Assert.Equal(1, cachedHost.LastFrameScratchRenders);
        Assert.Equal(1, cachedSource.Renders);

        var directSource = new TestVectorSource(40, 30, red);
        using EmbeddedUiHost directHost = NewHost(dev, Black(Scene(directSource, ImageVectorCacheMode.Direct)), (int)W, (int)H);
        UiFrame.Image direct = UiFrame.Host(dev, directHost, W, H);
        Assert.Equal(0, directHost.LastFrameScratchRenders);

        int cachedRed = Count(cached, p => p.R > 150 && p.G < 80);
        int directRed = Count(direct, p => p.R > 150 && p.G < 80);
        Console.WriteLine(S($"[scratch] red pixels cached={cachedRed} direct={directRed} (80 x 60 = 4800)"));
        Assert.InRange(cachedRed, 4700, 4900);
        Assert.InRange(Math.Abs(cachedRed - directRed), 0, 40);

        // Inside the image, both are the fill; the cached one went through a premultiplied BGRA surface and back.
        Assert.Equal(direct.Px(60, 50), cached.Px(60, 50));
        (int r, int g, int b) = cached.Px(60, 50);
        Assert.True(r > 200 && g < 50 && b < 50, S($"cached fill is {(r, g, b)}"));

        // Outside the control the window background shows through the surface's transparent surround.
        Assert.Equal((0, 0, 0), cached.Px(10, 10));
        Assert.Equal((0, 0, 0), cached.Px(110, 90));

        // The next frame blits the cache (no scratch render) and the pixels are unchanged.
        UiFrame.Image again = UiFrame.Host(dev, cachedHost, W, H);
        Assert.Equal(0, cachedHost.LastFrameScratchRenders);
        Assert.Equal(1, cachedSource.Renders);
        Assert.Equal(cached.Px(60, 50), again.Px(60, 50));
        Assert.Equal(cachedRed, Count(again, p => p.R > 150 && p.G < 80));
    }

    [SkippableFact]
    public void TwoCaches_InOneFrame_BothSurvive()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint W = 200;
        const uint H = 100;
        var green = new TestVectorSource(10, 10, Color.FromArgb(255, 20, 220, 40));
        var blue = new TestVectorSource(10, 10, Color.FromArgb(255, 40, 60, 230));
        Canvas canvas = new Canvas().Width(W).Height(H);
        canvas.Add(new Image { Source = green }.Width(60).Height(60).CanvasPosition(10, 20));
        canvas.Add(new Image { Source = blue }.Width(60).Height(60).CanvasPosition(120, 20));
        using EmbeddedUiHost host = NewHost(dev, Black(canvas), (int)W, (int)H);
        UiFrame.Image img = UiFrame.Host(dev, host, W, H);
        Assert.Equal(2, host.LastFrameScratchRenders);
        int greens = Count(img, p => p.G > 150 && p.R < 80 && p.B < 80);
        int blues = Count(img, p => p.B > 150 && p.R < 80 && p.G < 100);
        Console.WriteLine(S($"[scratch] two caches: green={greens} blue={blues} (60 x 60 = 3600 each)"));
        Assert.InRange(greens, 3500, 3700);
        Assert.InRange(blues, 3500, 3700);
    }

    [SkippableFact]
    public void BitmapCache_DrawsTheSubtree_LikeAnUncachedOne()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint W = 120;
        const uint H = 120;

        static Border Panel(bool cached)
        {
            var panel = new Border
            {
                Background = Color.FromArgb(255, 200, 160, 40),
                CornerRadius = 12,
                CacheMode = cached ? new BitmapCache() : null,
                Child = new Border { Background = Color.FromArgb(255, 30, 30, 200) }.Width(30).Height(30),
            };
            return panel.Width(80).Height(80).Margin(20);
        }

        using EmbeddedUiHost cachedHost = NewHost(dev, Black(Panel(true)), (int)W, (int)H);
        UiFrame.Image cached = UiFrame.Host(dev, cachedHost, W, H);
        Assert.True(cachedHost.LastFrameScratchRenders >= 1, "the BitmapCache did not render into a scratch surface");
        using EmbeddedUiHost plainHost = NewHost(dev, Black(Panel(false)), (int)W, (int)H);
        UiFrame.Image plain = UiFrame.Host(dev, plainHost, W, H);
        Assert.Equal(0, plainHost.LastFrameScratchRenders);

        int cachedGold = Count(cached, p => p.R > 150 && p.G > 120 && p.B < 100);
        int plainGold = Count(plain, p => p.R > 150 && p.G > 120 && p.B < 100);
        int cachedBlue = Count(cached, p => p.B > 150 && p.R < 80);
        int plainBlue = Count(plain, p => p.B > 150 && p.R < 80);
        Console.WriteLine(S($"[scratch] bitmap cache gold={cachedGold}/{plainGold} blue={cachedBlue}/{plainBlue}"));
        Assert.InRange(Math.Abs(cachedGold - plainGold), 0, 60);
        Assert.InRange(Math.Abs(cachedBlue - plainBlue), 0, 20);
        Assert.True(plainBlue > 800 && cachedBlue > 800);
        Assert.Equal((0, 0, 0), cached.Px(5, 5));   // outside the panel: the window background through the surface

        // The second frame blits the cache: no scratch render, same pixels.
        UiFrame.Image again = UiFrame.Host(dev, cachedHost, W, H);
        Assert.Equal(0, cachedHost.LastFrameScratchRenders);
        Assert.Equal(cachedGold, Count(again, p => p.R > 150 && p.G > 120 && p.B < 100));
    }
}
