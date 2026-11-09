// UiRenderSurface: a MewUI IRenderSurface over IGraphicsDevice. A surface wraps a color ITexture (RenderTarget |
// CopySource | ShaderResource) plus a matching stencil-capable depth-stencil ITexture (D24S8) for the MewVG stencil
// passes. It owns the UiMewVGContext (the NanoVG renderer) and the FontStashTextRenderer (glyph atlases), created once
// on first use and reused across frames. CreateContext returns a MewStageMewVGGraphicsContext attached to this surface;
// it pulls the frame's recorder at BeginFrame. The host sets the recorder via BeginGraphRecording(...) inside the `ui`
// render-graph pass, before the windows render.
//
// Readback: ReadbackInto copies the color texture into a Readback buffer with CopyTextureToBuffer (offscreen tests).

using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Rendering.MewVG;

using MewStage.Graphics;

namespace MewStage.Ui.Backend;

/// <summary>A renderable + readable offscreen surface for an embedded MewUI Window (B4).</summary>
internal sealed class UiRenderSurface : IRenderSurface
{
    private readonly IGraphicsDevice _device;
    private readonly byte[] _uiVs;
    private readonly byte[] _uiPs;
    private readonly bool _flipClipY;
    private readonly int _framesInFlight;

    private ITexture? _color;
    private ITexture? _depthStencil;
    private UiMewVGContext? _vg;
    private FontStashTextRenderer? _text;

    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public double DpiScale { get; }
    public RenderPixelFormat Format => RenderPixelFormat.Bgra8888Premultiplied;
    public SurfaceUsage Usage => SurfaceUsage.Offscreen | SurfaceUsage.ImageSource | SurfaceUsage.ReadbackSource;
    public SurfaceCapabilities Capabilities
        => SurfaceCapabilities.Renderable | SurfaceCapabilities.CpuReadable |
        SurfaceCapabilities.Alpha | SurfaceCapabilities.Premultiplied;
    public ulong Version { get; private set; }
    public bool IsDisposed { get; private set; }

    /// <summary>The color attachment texture handle (for the host's BeginRendering color attachment).</summary>
    public ITexture ColorTexture => _color ??= CreateColor();
    /// <summary>The depth-stencil attachment texture handle (D24S8; stencil cleared to 0 by the host pass).</summary>
    public ITexture DepthStencilTexture => _depthStencil ??= CreateDepthStencil();
    public UiMewVGContext Vg => _vg ??= CreateVg();
    public FontStashTextRenderer Text => _text ??= CreateText();

    public UiRenderSurface(
        IGraphicsDevice device, int pixelWidth, int pixelHeight, double dpiScale,
        byte[] uiVs, byte[] uiPs, bool flipClipY, int framesInFlight)
    {
        _device = device;
        _framesInFlight = framesInFlight < 1 ? 1 : framesInFlight;
        PixelWidth = Math.Max(1, pixelWidth);
        PixelHeight = Math.Max(1, pixelHeight);
        DpiScale = dpiScale <= 0 ? 1.0 : dpiScale;
        _uiVs = uiVs;
        _uiPs = uiPs;
        _flipClipY = flipClipY;
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
            Usage = ResourceUsage.RenderTarget | ResourceUsage.CopySource | ResourceUsage.ShaderResource,
            DebugName = "mew-ui-color",
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
            DebugName = "mew-ui-ds",
        });
    }

    private UiMewVGContext CreateVg()
    {
        var vg = UiMewVGContext.Create(_device, _uiVs, _uiPs,
            Graphics.Format.B8G8R8A8_UNorm,
            Graphics.Format.D24_UNorm_S8_UInt, _flipClipY, _framesInFlight);
        return vg;
    }

    // Glyph rasterization for this surface: one FontStashSharp system (and atlas) per face, created on first use.
    // The faces themselves live in the factory's FontCollection (UiFont carries its face).
    private FontStashTextRenderer CreateText()
    {
        return new(Vg);
    }

    /// <summary>
    /// Bind the active render-graph recorder for this frame. Call inside the `ui` pass after BeginRendering
    /// (color + D24S8 depth, stencil cleared to 0) and before Window.RenderFrameToPresentSurface. The context created
    /// by the subsequent CreateContext call records its NVG flush onto this recorder.
    /// </summary>
    public void BeginGraphRecording(IGraphicsRecorder recorder)
    {
        Recorder = recorder;
    }

    /// <summary>The recorder bound for the current frame (contexts pull it at BeginFrame), or null outside one.</summary>
    internal IGraphicsRecorder? Recorder { get; private set; }

    /// <summary>Open a multi-window composite batch on this surface's NanoVG sink: every window rendered via
    /// <c>Window.RenderFrameToPresentSurface</c> until <see cref="EndWindowBatch"/>
    /// accumulates into ONE NanoVG frame, flushed once (one frames-in-flight ring-slot advance). One window
    /// between Begin/End is byte-identical to the non-batched single flush.</summary>
    public void BeginWindowBatch()
    {
        Vg.Renderer.BeginBatch();
    }

    /// <summary>Close the composite batch and flush the accumulated windows once (upload + record in order).</summary>
    public void EndWindowBatch()
    {
        Vg.Renderer.EndBatch();
    }

    /// <summary>Record the texture uploads the windows queued (glyph atlas pages, coverage masks) onto the frame's own
    /// command list. Call after the windows rendered and BEFORE the `ui` pass's BeginRendering (copies are not allowed
    /// inside a render pass); see NanoVgRenderer.RecordPendingUploads.</summary>
    public void RecordPendingUploads(ICommandList cmd)
    {
        Vg.Renderer.RecordPendingUploads(cmd);
    }

    /// <summary>A context drawing into this surface. Windows keep it across frames (RenderFrameToPresentSurface);
    /// it pulls the frame's recorder, MewVG context and text renderer from this surface at every BeginFrame.</summary>
    public IGraphicsContext CreateContext()
    {
        var ctx = new MewStageMewVGGraphicsContext();
        ctx.AttachSurface(this);
        return ctx;
    }

    /// <summary>True if the surface has been fully realized (textures + sink created) — for diagnostics/tests.</summary>
    public bool IsRealized => _color is not null && _vg is not null;

    internal void BumpVersion()
    {
        Version++;
    }

    /// <summary>Copy the color texture into <paramref name="destination"/> (BGRA, tight rows). The caller must
    /// have submitted + completed the frame's GPU work (so the readback buffer is populated).</summary>
    public bool ReadbackInto(Span<byte> destination)
    {
        if (_color is null)
        {
            return false;
        }

        int strideBytes = PixelWidth * 4;
        long needed = (long)strideBytes * PixelHeight;
        if (destination.Length < needed)
        {
            return false;
        }

        IBuffer rb = _device.CreateBuffer(new BufferDesc
        { SizeBytes = (ulong)needed, Memory = MemoryClass.Readback, Usage = ResourceUsage.CopyDest });
        try
        {
            ICommandList cmd = _device.GraphicsQueue.AcquireCommandList();
            cmd.Begin();
            Span<ResourceBarrierDesc> pre = [ResourceBarrierDesc.Transition(_color.Handle, ResourceState.Common, ResourceState.CopySource)];
            cmd.ResourceBarrier(pre);
            cmd.CopyTextureToBuffer(_color, rb, (uint)PixelWidth, (uint)PixelHeight);
            cmd.Close();
            IFence fence = _device.CreateFence(0);
            _device.GraphicsQueue.Submit(new[] { cmd }, default, new[] { new FenceSignal(fence, 1) });
            fence.Wait(1);
            if (rb is IMappedBuffer mapped)
            {
                unsafe
                {
                    new ReadOnlySpan<byte>((void*)mapped.MappedPointer, (int)needed).CopyTo(destination);
                }
            }

            fence.Dispose();
            return true;
        }
        finally
        {
            rb.Dispose();
        }
    }

    public void Dispose()
    {
        if (IsDisposed)
        {
            return;
        }

        IsDisposed = true;
        _text?.Dispose();
        _vg?.Dispose();
        _color?.Dispose();
        _depthStencil?.Dispose();
    }
}
