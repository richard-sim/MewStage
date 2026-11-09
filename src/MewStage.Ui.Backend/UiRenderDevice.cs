// UiRenderDevice: the MewUI IRenderDevice implemented over MewStage.Graphics' IGraphicsDevice. This is the surface /
// context / image-view / readback half of the MewUI graphics backend (UiGraphicsFactory composes this device and adds
// brush / font / image-decode creation). MewUI's Window.RenderFrameToSurface calls GraphicsFactory.CreateContext(target)
// where the target is the IRenderSurface created here, which returns a MewStageMewVGGraphicsContext bound to the
// surface's NanoVG renderer and the active render-graph recorder.
//
//   * CreateSurface  -> a UiScratchSurface for MewUI's cache descriptors (a vector image's bitmap, an element's
//     BitmapCache: SurfaceUsage.ImageSource), else a UiRenderSurface (color ITexture + D24S8 stencil target + the
//     MewVG context / text renderer; the host's window surface).
//   * CreateContext(IRenderSurface) -> the surface's context. A window surface requires the host to have bound the
//     recorder; a scratch surface requires the frame to be open (BeginFrameRecording, which the host calls inside the
//     `ui` pass) and renders through the shared scratch MewVG context (ScratchVg) into the frame's command list.
//   * CreateImageView(IRenderSurface / IPixelBufferSource / IExternalRasterSource) -> MewVGImage views (external
//     rasters and scratch surfaces: zero-copy over a registered GPU texture, ExternalTextures.cs).
//   * TryReadPixels / RequestReadback -> CPU readback of the surface color texture (offscreen tests).
//   * Effects -> null (no GPU effect device; MewUI falls back to its CPU reference path, which the contract allows).

using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Rendering.MewVG;
using Aprillz.MewUI.Resources;

using MewStage.Graphics;

using Format = MewStage.Graphics.Format;

namespace MewStage.Ui.Backend;

/// <summary>The MewUI render device (surfaces/contexts/image-views/readback) over <see cref="IGraphicsDevice"/>.</summary>
public sealed class UiRenderDevice : IRenderDevice, IDisposable
{
    private readonly IGraphicsDevice _device;
    private readonly byte[] _uiVs;
    private readonly byte[] _uiPs;
    private readonly bool _flipClipY;
    private readonly int _framesInFlight;
    private readonly RenderResourceCache _cache = new();
    private readonly HashSet<UiScratchSurface> _liveScratch = [];   // every scratch surface not yet disposed (see Dispose)
    private UiMewVGContext? _scratchVg;
    private FontStashTextRenderer? _scratchText;
    private bool _disposed;

    /// <summary>Creates the device over <paramref name="device"/>.</summary>
    /// <param name="device">The application's GPU device.</param>
    /// <param name="uiVs">The cooked ui.hlsl vertex shader for this device's backend.</param>
    /// <param name="uiPs">The cooked ui.hlsl pixel shader.</param>
    /// <param name="flipClipY">True on DX12 / Metal (clip Y up), false on Vulkan.</param>
    /// <param name="framesInFlight">The scheduler's frame depth (sizes the per-frame upload rings).</param>
    public UiRenderDevice(IGraphicsDevice device, byte[] uiVs, byte[] uiPs, bool flipClipY = false, int framesInFlight = 2)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _framesInFlight = framesInFlight < 1 ? 1 : framesInFlight;
        _uiVs = uiVs ?? throw new ArgumentNullException(nameof(uiVs));
        _uiPs = uiPs ?? throw new ArgumentNullException(nameof(uiPs));
        _flipClipY = flipClipY;
    }

    /// <inheritdoc />
    public IRenderSurface CreateSurface(RenderSurfaceDescriptor descriptor)
    {
        if ((descriptor.Usage & SurfaceUsage.ImageSource) != 0)
        {
            bool hasAlpha = (descriptor.RequiredCapabilities & SurfaceCapabilities.Alpha) != 0
                || descriptor.Format is RenderPixelFormat.Bgra8888Premultiplied or RenderPixelFormat.Rgba8888Premultiplied;
            var scratch = new UiScratchSurface(_device, descriptor.PixelWidth, descriptor.PixelHeight, descriptor.DpiScale, hasAlpha, descriptor.DebugName, OnScratchDisposed);
            lock (_liveScratch)
            {
                _liveScratch.Add(scratch);
            }

            return scratch;
        }

        return new UiRenderSurface(_device, descriptor.PixelWidth, descriptor.PixelHeight, descriptor.DpiScale,
                _uiVs, _uiPs, _flipClipY, _framesInFlight);
    }

    /// <inheritdoc />
    public IGraphicsContext CreateContext(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        return CreateContextCore(RenderSurfaceResource.ResolveBackendSurface(surface));
    }

    /// <inheritdoc />
    public IGraphicsContext CreateContext(IRenderTarget target)
    {
        return target is IRenderSurface surface
            ? CreateContext(surface)
            : throw new NotSupportedException($"{nameof(UiRenderDevice)} only renders into its own surfaces.");
    }

    private IGraphicsContext CreateContextCore(IRenderSurface backend)
    {
        switch (backend)
        {
            case UiRenderSurface window:
                return window.CreateContext();
            case UiScratchSurface scratch:
            {
                var ctx = new MewStageMewVGGraphicsContext();
                ctx.AttachScratch(scratch, this);
                return ctx;
            }

            default:
                throw new NotSupportedException($"{nameof(UiRenderDevice)} only renders into a {nameof(UiRenderSurface)} or a {nameof(UiScratchSurface)}.");
        }
    }

    /// <inheritdoc />
    public IImage CreateImageView(IRenderSurface surface)
    {
        ArgumentNullException.ThrowIfNull(surface);
        IRenderSurface backend = RenderSurfaceResource.ResolveBackendSurface(surface);
        if (backend is UiScratchSurface scratch)
        {
            return scratch.CreateImage();
        }

        return backend is IPixelBufferSource pixelSource
                ? CreateImageView(pixelSource)
                : throw new NotSupportedException($"{nameof(UiRenderDevice)} can only create image views for scratch surfaces and pixel-backed surfaces.");
    }

    // ---- the frame, for scratch renders ----

    /// <summary>The recorder of the frame being recorded (null outside <see cref="BeginFrameRecording"/>).</summary>
    internal IGraphicsRecorder? FrameRecorder { get; private set; }

    /// <summary>The command list of the frame being recorded (null outside <see cref="BeginFrameRecording"/>).</summary>
    internal ICommandList? FrameCommandList { get; private set; }

    /// <summary>Scratch renders (vector-image and bitmap-cache rasterizations) recorded in the frame being recorded,
    /// or in the last one once it closed.</summary>
    public int ScratchRendersThisFrame { get; private set; }

    /// <summary>Scratch renders recorded since the device was created.</summary>
    public int TotalScratchRenders { get; private set; }

    /// <summary>Scratch surfaces created and not yet disposed (leased by MewUI's caches or sitting in its pool). Each
    /// realized one holds a color texture registered in <see cref="ExternalTextures"/>.</summary>
    public int LiveScratchSurfaces
    {
        get
        {
            lock (_liveScratch)
            {
                return _liveScratch.Count;
            }
        }
    }

    /// <summary>Open the frame: a scratch surface rendered from now until <see cref="EndFrameRecording"/> (MewUI's
    /// caches rasterize from OnRender) records onto <paramref name="commandList"/> through <paramref name="recorder"/>.
    /// The host calls this inside the `ui` pass before the windows render, while no render pass is open.</summary>
    internal void BeginFrameRecording(IGraphicsRecorder recorder, ICommandList commandList)
    {
        FrameRecorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        FrameCommandList = commandList ?? throw new ArgumentNullException(nameof(commandList));
        ScratchRendersThisFrame = 0;
    }

    /// <summary>Close the frame (a scratch render outside one throws).</summary>
    internal void EndFrameRecording()
    {
        FrameRecorder = null;
        FrameCommandList = null;
    }

    /// <summary>The shared MewVG context every scratch surface renders through (created on the first scratch
    /// render; fresh buffers per flush, see <see cref="UiMewVGContext.CreateForScratch"/>).</summary>
    internal UiMewVGContext ScratchVg => _scratchVg ??= UiMewVGContext.CreateForScratch(_device, _uiVs, _uiPs,
        Format.B8G8R8A8_UNorm, Format.D24_UNorm_S8_UInt, _flipClipY, _framesInFlight);

    /// <summary>The text renderer (glyph atlases) of the scratch context.</summary>
    internal FontStashTextRenderer ScratchText => _scratchText ??= new FontStashTextRenderer(ScratchVg);

    /// <summary>Count one scratch render (the scratch context calls it as it closes a render).</summary>
    internal void CountScratchRender()
    {
        ScratchRendersThisFrame++;
        TotalScratchRenders++;
    }

    /// <summary>Drop every cache nothing leases: the scratch pool (surfaces that closed windows returned) and idle
    /// cached resources. Their textures leave <see cref="ExternalTextures"/> now and are destroyed once the GPU is past
    /// them (deferred destroy). Caches a live element still leases stay. Call between frames.</summary>
    public void TrimCaches()
    {
        if (_disposed)
        {
            return;
        }

        _cache.Trim(RenderCacheTrimReason.MemoryPressure);   // every unleased entry + the scratch pool (Maintain would keep recently used entries)
    }

    /// <inheritdoc />
    public IImage CreateImageView(IPixelBufferSource source)
    {
        return new MewVGImage(source);
    }

    /// <summary>An external GPU raster as a zero-copy image. A <see cref="GpuTextureSource"/> (the
    /// producer type) is wrapped directly; any other source must lease a <see cref="ExternalTextures"/> key as its
    /// native handle (a handle the registry does not know is refused: this backend never dereferences a raw pointer).</summary>
    public IImage CreateImageView(IExternalRasterSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source is GpuTextureSource own)
        {
            return new MewVGImage(own);
        }

        using IExternalRasterLease lease = source.Acquire();
        if (lease.NativeHandle != 0 && ExternalTextures.TryGet(lease.NativeHandle, out _))
        {
            return new MewVGImage(new ExternalRasterAdapter(source, lease.NativeHandle));
        }

        throw new NotSupportedException($"{nameof(UiRenderDevice)} wraps only external rasters registered in {nameof(ExternalTextures)}.");
    }

    /// <inheritdoc />
    public bool TryReadPixels(IRenderSurface source, Span<byte> destination, int destinationStrideBytes)
    {
        if (source is not UiRenderSurface vs)
        {
            return false;
        }

        if (destinationStrideBytes < vs.PixelWidth * 4)
        {
            return false;
        }

        return vs.ReadbackInto(destination);
    }

    /// <inheritdoc />
    public IRenderOperation RequestReadback(IRenderSurface source)
    {
        return RenderOperation.Completed;
    }

    /// <inheritdoc />
    public IRenderOperation FlushAsyncWork()
    {
        return RenderOperation.Completed;
    }

    /// <inheritdoc />
    public IRenderResourceCache? ResourceCache => _cache;

    /// <inheritdoc />
    public IRenderEffectDevice? Effects => null;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cache.Dispose();

        // The pool only owns the scratch surfaces that were returned to it. The ones a live Image or BitmapCache
        // still leases would be disposed by the element on unload, which never comes for windows torn down with
        // the host; the GPU textures are this device's to free before the application's device goes.
        UiScratchSurface[] survivors;
        lock (_liveScratch)
        {
            survivors = [.. _liveScratch];
            _liveScratch.Clear();
        }

        foreach (UiScratchSurface scratch in survivors)
        {
            scratch.Dispose();
        }

        _scratchText?.Dispose();
        _scratchVg?.Dispose();
    }

    private void OnScratchDisposed(UiScratchSurface scratch)
    {
        lock (_liveScratch)
        {
            _ = _liveScratch.Remove(scratch);
        }
    }
}
