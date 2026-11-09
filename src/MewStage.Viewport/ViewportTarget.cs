// A viewport's offscreen color target: allocated on first arrange, re-allocated when the arranged pixel size changes,
// freed on detach. The texture is shown in the UI through a zero-copy MewVG image (Ui.Backend's GpuTextureSource: the
// image's slot points at the texture's shader-resource view); old textures are retired through the device's
// deferred-destroy queue so a frame still in flight never samples freed memory.
using System;

using Aprillz.MewUI.Rendering;

using MewStage.Graphics;
using MewStage.Ui.Backend;

namespace MewStage.Viewport;

/// <summary>The color target of one viewport and the UI image over it (see the file header).</summary>
public sealed class ViewportTarget : IDisposable
{
    private readonly IGraphicsDevice _device;
    private readonly Format _format;
    private GpuTextureSource? _source;

    /// <summary>Creates an empty target (no texture until <see cref="Ensure"/>).</summary>
    public ViewportTarget(IGraphicsDevice device, Format format)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _format = format;
    }

    /// <summary>Current width, pixels (0 = none).</summary>
    public int Width { get; private set; }

    /// <summary>Current height, pixels (0 = none).</summary>
    public int Height { get; private set; }

    /// <summary>The color texture (RenderTarget | ShaderResource | CopySource), or null.</summary>
    public ITexture? Color { get; private set; }

    /// <summary>Its shader-resource view, or null.</summary>
    public ITextureView? ColorSrv { get; private set; }

    /// <summary>The MewUI image over the texture (assigned to <see cref="ViewportControl.Image"/>), or null.</summary>
    public IImage? Image { get; private set; }

    /// <summary>Number of (re)allocations so far (resize tests).</summary>
    public int Generation { get; private set; }

    /// <summary>Allocates or re-allocates for <paramref name="width"/> x <paramref name="height"/>; true when a new texture (and image) was made.</summary>
    public bool Ensure(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "viewport target size must be positive");
        }

        if (Color is not null && width == Width && height == Height)
        {
            return false;
        }

        Retire();
        Color = _device.CreateTexture(new TextureDesc
        {
            Dimension = TextureDimension.Tex2D,
            Format = _format,
            Width = (uint)width,
            Height = (uint)height,
            DepthOrArrayLayers = 1,
            MipLevels = 1,
            SampleCount = 1,
            Usage = ResourceUsage.RenderTarget | ResourceUsage.ShaderResource | ResourceUsage.CopySource,
            DebugName = "viewport-color",
        });
        ColorSrv = _device.CreateView(Color, new TextureViewDesc
        {
            Kind = ViewKind.Srv,
            Format = _format,
            Dimension = TextureDimension.Tex2D,
            BaseMip = 0,
            MipCount = 1,
            BaseArrayLayer = 0,
            ArrayLayerCount = 1,
        });
        _source = new GpuTextureSource(Color, ColorSrv, width, height);
        Image = _source.CreateImage();
        Width = width;
        Height = height;
        Generation++;
        return true;
    }

    /// <summary>Frees the texture (deferred past the frames in flight) and the image.</summary>
    public void Dispose()
    {
        Retire();
    }

    private void Retire()
    {
        Image?.Dispose();
        _source?.Dispose();
        if (ColorSrv is not null)
        {
            _device.DeferDestroy(ColorSrv);
        }

        if (Color is not null)
        {
            _device.DeferDestroy(Color);
        }

        Image = null;
        _source = null;
        ColorSrv = null;
        Color = null;
        Width = 0;
        Height = 0;
    }
}
