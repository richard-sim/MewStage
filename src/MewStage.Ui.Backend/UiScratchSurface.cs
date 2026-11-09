// UiScratchSurface: the offscreen surface MewUI's caches draw into on the embedded host. MewUI asks the graphics
// factory for one (AcquireScratchSurface -> IRenderDevice.CreateSurface with a CachedImage descriptor) when an Image
// rasterizes a vector source (SVG) into a bitmap, when an element with CacheMode = BitmapCache caches its subtree, and
// for a few filter intermediates; it then renders into it with a context from CreateContext(surface) and shows the
// result through CreateImageView(surface).
//
// The embedded host renders inside ONE render-graph pass (`ui`), and MewUI rasterizes a cache SYNCHRONOUSLY from the
// control's OnRender, which the host runs inside that pass's execute BEFORE the pass opens its own BeginRendering
// (the windows' traversal is CPU-only: MewVG appends draws, the host flushes them once after). So a scratch render is
// recorded into the frame's command list right there: its own BeginRendering on this surface's color + stencil
// textures, the device's shared scratch MewVG context flushed into it, EndRendering, and a barrier to ShaderResource,
// the state the `ui` pass samples it in a moment later (MewStageMewVGGraphicsContext.Platform.cs, the scratch branch).
// The textures are the backend's, not the graph's, so this type tracks their states and emits the barriers itself.
//
// The image side is the same zero-copy path the viewport uses: a GpuTextureSource over the color texture's SRV, a
// registry key MewVG receives as the native handle (ExternalTextures). The surface is usually larger than the content
// (the pool over-allocates); the caller draws the content sub-rectangle.
//
// Off-thread rasterization (MewUI's CachedDeferred and the size-change rebuild) is not possible here: GPU work can
// only be recorded inside the frame. UiGraphicsFactory.AcquireBackgroundRenderScope refuses, MewUI marks the control
// as "async unsupported" and rasterizes synchronously from then on.
using Aprillz.MewUI.Rendering;

using MewStage.Graphics;

namespace MewStage.Ui.Backend;

/// <summary>An offscreen color + stencil target MewUI's caches render into, shown back as a zero-copy image (see the file header).</summary>
internal sealed class UiScratchSurface : IRenderSurface
{
    private static readonly ClearColor Transparent = new(0f, 0f, 0f, 0f);

    private readonly IGraphicsDevice _device;
    private readonly Action<UiScratchSurface>? _onDisposed;
    private ITexture? _color;
    private ITexture? _depthStencil;
    private ITextureView? _colorSrv;
    private GpuTextureSource? _source;
    private ResourceState _colorState = ResourceState.Common;
    private ResourceState _depthState = ResourceState.Common;
    private bool _rendering;

    /// <summary>Create a surface of the given pixel size. The textures are allocated on first use;
    /// <paramref name="onDisposed"/> (the owning device's bookkeeping) runs once when the surface is disposed.</summary>
    public UiScratchSurface(IGraphicsDevice device, int pixelWidth, int pixelHeight, double dpiScale, bool hasAlpha, string? debugName, Action<UiScratchSurface>? onDisposed = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _onDisposed = onDisposed;
        PixelWidth = Math.Max(1, pixelWidth);
        PixelHeight = Math.Max(1, pixelHeight);
        DpiScale = dpiScale <= 0 ? 1.0 : dpiScale;
        HasAlpha = hasAlpha;
        DebugName = debugName ?? "mew-scratch";
    }

    /// <inheritdoc />
    public int PixelWidth { get; }

    /// <inheritdoc />
    public int PixelHeight { get; }

    /// <inheritdoc />
    public double DpiScale { get; }

    /// <summary>True when the surface is cleared to transparent and composites with its alpha.</summary>
    public bool HasAlpha { get; }

    /// <summary>The debug name MewUI gave the surface (the texture debug names carry it).</summary>
    public string DebugName { get; }

    /// <inheritdoc />
    public RenderPixelFormat Format => HasAlpha ? RenderPixelFormat.Bgra8888Premultiplied : RenderPixelFormat.Bgra8888;

    /// <inheritdoc />
    public SurfaceUsage Usage => SurfaceUsage.Offscreen | SurfaceUsage.ImageSource | SurfaceUsage.CachedImageSource;

    /// <inheritdoc />
    public SurfaceCapabilities Capabilities
        => SurfaceCapabilities.Renderable | SurfaceCapabilities.GpuSampleable | SurfaceCapabilities.CacheableImageSource
           | (HasAlpha ? SurfaceCapabilities.Alpha | SurfaceCapabilities.Premultiplied : SurfaceCapabilities.None);

    /// <summary>Bumped after every render into the surface.</summary>
    public ulong Version { get; private set; }

    /// <inheritdoc />
    public bool IsDisposed { get; private set; }

    /// <summary>How many times the surface has been rendered into (tests).</summary>
    public int RenderCount { get; private set; }

    /// <summary>True once the textures exist (the first render allocates them).</summary>
    public bool IsRealized => _color is not null;

    /// <summary>A MewUI image over the color texture (zero copy). Each call returns a new image over the same source;
    /// MewUI creates one per rasterization and disposes the previous.</summary>
    public IImage CreateImage()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Source.CreateImage();
    }

    /// <summary>Open the render into this surface on the frame's command list: barriers, BeginRendering (color cleared
    /// to transparent, stencil to 0), viewport. Pair with <see cref="EndRender"/>.</summary>
    public void BeginRender(ICommandList cmd, IGraphicsRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(recorder);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (_rendering)
        {
            throw new InvalidOperationException("the scratch surface is already being rendered into");
        }

        ITexture color = _color ??= CreateColor();
        ITexture depth = _depthStencil ??= CreateDepthStencil();
        Span<ResourceBarrierDesc> barriers = stackalloc ResourceBarrierDesc[2];
        int n = 0;
        if (_colorState != ResourceState.RenderTarget)
        {
            barriers[n++] = ResourceBarrierDesc.Transition(color.Handle, _colorState, ResourceState.RenderTarget);
            _colorState = ResourceState.RenderTarget;
        }

        if (_depthState != ResourceState.DepthWrite)
        {
            barriers[n++] = ResourceBarrierDesc.Transition(depth.Handle, _depthState, ResourceState.DepthWrite);
            _depthState = ResourceState.DepthWrite;
        }

        if (n > 0)
        {
            cmd.ResourceBarrier(barriers[..n]);
        }

        Span<ColorAttachment> ca = [new ColorAttachment { TextureHandle = color.Handle, Clear = true, ClearValue = Transparent }];
        var da = new DepthAttachment { TextureHandle = depth.Handle, Clear = true, ClearDepth = 0f, HasStencil = true, ClearStencilOnLoad = true, ClearStencil = 0 };
        recorder.BeginRendering(ca, da, (uint)PixelWidth, (uint)PixelHeight);
        recorder.SetViewportScissor(0, 0, PixelWidth, PixelHeight);
        _rendering = true;
    }

    /// <summary>Close the render: EndRendering and the barrier into ShaderResource, where the `ui` pass samples it.</summary>
    public void EndRender(ICommandList cmd, IGraphicsRecorder recorder)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        ArgumentNullException.ThrowIfNull(recorder);
        if (!_rendering)
        {
            throw new InvalidOperationException("EndRender without BeginRender");
        }

        recorder.EndRendering();
        Span<ResourceBarrierDesc> barriers = [ResourceBarrierDesc.Transition(_color!.Handle, _colorState, ResourceState.ShaderResource)];
        cmd.ResourceBarrier(barriers);
        _colorState = ResourceState.ShaderResource;
        _rendering = false;
        Version++;
        RenderCount++;
    }

    /// <summary>Free the textures through the device's deferred destroy (a frame in flight may still sample them).</summary>
    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _source?.Dispose();
        _source = null;
        if (_colorSrv is not null)
        {
            _device.DeferDestroy(_colorSrv);
            _colorSrv = null;
        }

        if (_color is not null)
        {
            _device.DeferDestroy(_color);
            _color = null;
        }

        if (_depthStencil is not null)
        {
            _device.DeferDestroy(_depthStencil);
            _depthStencil = null;
        }

        _onDisposed?.Invoke(this);
    }

    private GpuTextureSource Source
    {
        get
        {
            if (_source is null)
            {
                ITexture color = _color ??= CreateColor();
                _colorSrv ??= _device.CreateView(color, new TextureViewDesc
                {
                    Kind = ViewKind.Srv,
                    Format = Graphics.Format.B8G8R8A8_UNorm,
                    Dimension = TextureDimension.Tex2D,
                    BaseMip = 0,
                    MipCount = 1,
                    BaseArrayLayer = 0,
                    ArrayLayerCount = 1,
                });
                _source = new GpuTextureSource(color, _colorSrv, PixelWidth, PixelHeight, premultiplied: true, hasAlpha: HasAlpha);
            }

            return _source;
        }
    }

    private ITexture CreateColor()
    {
        return _device.CreateTexture(new TextureDesc
        {
            Dimension = TextureDimension.Tex2D,
            Format = Graphics.Format.B8G8R8A8_UNorm,
            Width = (uint)PixelWidth,
            Height = (uint)PixelHeight,
            DepthOrArrayLayers = 1,
            MipLevels = 1,
            SampleCount = 1,
            Usage = ResourceUsage.RenderTarget | ResourceUsage.ShaderResource,
            DebugName = DebugName + "-color",
        });
    }

    private ITexture CreateDepthStencil()
    {
        return _device.CreateTexture(new TextureDesc
        {
            Dimension = TextureDimension.Tex2D,
            Format = Graphics.Format.D24_UNorm_S8_UInt,
            Width = (uint)PixelWidth,
            Height = (uint)PixelHeight,
            DepthOrArrayLayers = 1,
            MipLevels = 1,
            SampleCount = 1,
            Usage = ResourceUsage.DepthStencil,
            DebugName = DebugName + "-ds",
        });
    }
}
