using Aprillz.MewUI.Rendering;
using Aprillz.MewVG;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Headless;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Headless;

/// <summary>
/// The external-texture registry behind a viewport-in-UI image: a producer's texture reaches the NanoVG renderer as a
/// registry KEY, never a raw pointer, so a stale key resolves to nothing instead of to freed memory, and the registry
/// returns to empty when every source is disposed.
/// </summary>
public sealed class ExternalTexturesTests
{
    private static ITexture MakeTexture(HeadlessDevice dev)
    {
        return dev.CreateTexture(new TextureDesc
        {
            Dimension = TextureDimension.Tex2D,
            Format = Format.R8G8B8A8_UNorm,
            Width = 64,
            Height = 32,
            DepthOrArrayLayers = 1,
            MipLevels = 1,
            SampleCount = 1,
            Usage = ResourceUsage.RenderTarget | ResourceUsage.ShaderResource,
        });
    }

    [Fact]
    public void ExternalTextures_Count_ReturnsToZero_OnDetach()
    {
        using var dev = new HeadlessDevice();
        int before = ExternalTextures.Count;
        using ITexture tex = MakeTexture(dev);
        using ITextureView view = dev.CreateView(tex, new TextureViewDesc { Kind = ViewKind.Srv, Format = Format.R8G8B8A8_UNorm, Dimension = TextureDimension.Tex2D, MipCount = 1, ArrayLayerCount = 1 });

        var a = new GpuTextureSource(tex, view, 64, 32);
        var b = new GpuTextureSource(tex, view, 64, 32);
        Assert.Equal(before + 2, ExternalTextures.Count);
        Assert.NotEqual(a.Key, b.Key);
        Assert.True(ExternalTextures.TryGet(a.Key, out ExternalTextures.Entry entry));
        Assert.Same(view, entry.View);
        Assert.Equal(64, entry.Width);

        a.Dispose();
        b.Dispose();
        Assert.Equal(before, ExternalTextures.Count);
    }

    [Fact]
    public void ExternalTextures_StaleKey_ResolvesToNothing()
    {
        using var dev = new HeadlessDevice();
        using ITexture tex = MakeTexture(dev);
        using ITextureView view = dev.CreateView(tex, new TextureViewDesc { Kind = ViewKind.Srv, Format = Format.R8G8B8A8_UNorm, Dimension = TextureDimension.Tex2D, MipCount = 1, ArrayLayerCount = 1 });
        var source = new GpuTextureSource(tex, view, 64, 32);
        nint key = source.Key;
        source.Dispose();

        Assert.False(ExternalTextures.TryGet(key, out _));
        Assert.Equal(0, source.GetTextureHandle());    // a disposed source reports no handle

        // The renderer refuses the stale key: no image slot, so MewVGImage falls back to its CPU path instead of
        // sampling freed memory.
        using var vg = UiMewVGContext.Create(dev, TestAssets.DummyShader, TestAssets.DummyShader, Format.B8G8R8A8_UNorm, Format.D24_UNorm_S8_UInt);
        Assert.Equal(0, vg.CreateImageFromNativeHandle(key, 64, 32, MewVGImageFlags.None));
    }

    [Fact]
    public void ExternalTextures_LiveKey_BecomesANoDeleteImage()
    {
        using var dev = new HeadlessDevice();
        using ITexture tex = MakeTexture(dev);
        using ITextureView view = dev.CreateView(tex, new TextureViewDesc { Kind = ViewKind.Srv, Format = Format.R8G8B8A8_UNorm, Dimension = TextureDimension.Tex2D, MipCount = 1, ArrayLayerCount = 1 });
        using var source = new GpuTextureSource(tex, view, 64, 32);
        using var vg = UiMewVGContext.Create(dev, TestAssets.DummyShader, TestAssets.DummyShader, Format.B8G8R8A8_UNorm, Format.D24_UNorm_S8_UInt);

        int image = vg.CreateImageFromNativeHandle(source.Key, 0, 0, MewVGImageFlags.None);
        Assert.NotEqual(0, image);
        Assert.True(vg.ImageSize(image, out int w, out int h));
        Assert.Equal((64, 32), (w, h));                 // the size comes from the registry when the caller passes 0
        Assert.Equal((int)tex.Handle, vg.ImageHandle(image));

        // Deleting the image drops the slot only; the producer's texture is untouched (it is disposed by the test).
        vg.DeleteImage(image);
        Assert.False(vg.ImageSize(image, out _, out _));
        Assert.True(ExternalTextures.TryGet(source.Key, out _));

        // The MewUI image view over the source is the zero-copy path's entry point.
        IImage img = source.CreateImage();
        Assert.Equal(64, img.PixelWidth);
        Assert.Equal(32, img.PixelHeight);
    }
}
