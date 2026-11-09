// A GPU texture owned by the application shown as a MewUI image WITHOUT ownership. A 3D viewport renders offscreen into
// an ITexture; this source hands that texture's SRV to the MewVG renderer so the `ui` pass samples it directly (zero
// copy). The texture stays owned by its producer; the NanoVG image slot is NoDelete.
//
// Path (all vendored code unchanged): UiRenderDevice.CreateImageView(IExternalRasterSource) -> MewVGImage(this as
// IPixelBufferSource) -> MewVGImage.GetOrCreateImageId -> IGpuTextureSource.GetTextureHandle() (our registry key) ->
// MewVGContext.CreateImageFromNativeHandle (UiMewVGContext override) -> NanoVgRenderer.CreateExternalImage(key) -> a
// NoDelete image slot whose SRV is the producer's view. The key is a registry id, never a raw GPU pointer, so a stale
// key resolves to nothing instead of to freed memory.
//
// State contract: the producer leaves the texture in ResourceState.ShaderResource for the `ui` pass (in a render graph
// the ui pass declares the read, EmbeddedUiHost.AddSampledTexture) and owns every other transition.

using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Resources;

using MewStage.Graphics;

namespace MewStage.Ui.Backend;

/// <summary>The process-wide table of externally owned GPU textures the MewVG renderer may wrap (key -> SRV).</summary>
public static class ExternalTextures
{
    /// <summary>One registered texture.</summary>
    public readonly record struct Entry(ITexture? Texture, ITextureView View, int Width, int Height);

    private static readonly Lock Gate = new();
    private static readonly Dictionary<nint, Entry> Entries = [];
    private static long s_next;

    /// <summary>Registers a texture view; returns its key (never 0).</summary>
    public static nint Register(ITexture? texture, ITextureView view, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "external texture size must be positive");
        }

        lock (Gate)
        {
            nint key = (nint)(++s_next);
            Entries[key] = new Entry(texture, view, width, height);
            return key;
        }
    }

    /// <summary>Removes a key (the NanoVG images made from it keep their view reference until deleted; the producer
    /// must defer-destroy the texture past the frames in flight).</summary>
    public static void Unregister(nint key)
    {
        lock (Gate)
        {
            Entries.Remove(key);
        }
    }

    /// <summary>Looks a key up.</summary>
    public static bool TryGet(nint key, out Entry entry)
    {
        lock (Gate)
        {
            return Entries.TryGetValue(key, out entry);
        }
    }

    /// <summary>Number of registered textures (tests: leak checks).</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Entries.Count;
            }
        }
    }
}

/// <summary>An externally owned GPU texture as a MewUI raster source. Create one per texture allocation (a resize
/// makes a new texture, so a new source and a new image); dispose it when the texture is retired.</summary>
public sealed class GpuTextureSource : IExternalRasterSource, IPixelBufferSource, IGpuTextureSource
{
    private readonly ExternalRasterPlane[] _planes;
    private byte[]? _cpuFallback;
    private bool _disposed;

    /// <summary>Wraps <paramref name="view"/> (an SRV of <paramref name="texture"/>) of the given pixel size.</summary>
    /// <param name="texture">The texture (null for a view whose texture is not tracked).</param>
    /// <param name="view">The shader-resource view the UI samples.</param>
    /// <param name="pixelWidth">The width in pixels.</param>
    /// <param name="pixelHeight">The height in pixels.</param>
    /// <param name="premultiplied">True when the texels carry premultiplied alpha (what the UI pass writes).</param>
    /// <param name="hasAlpha">True when the texture has meaningful alpha (a scratch surface cleared to transparent);
    /// false for an opaque world target.</param>
    public GpuTextureSource(ITexture? texture, ITextureView view, int pixelWidth, int pixelHeight, bool premultiplied = true, bool hasAlpha = false)
    {
        Key = ExternalTextures.Register(texture, view, pixelWidth, pixelHeight);
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
        IsPremultiplied = premultiplied;
        HasAlpha = hasAlpha;
        _planes = new[] { new ExternalRasterPlane(0, Key, pixelWidth, pixelHeight, pixelWidth * 4, Format) };
    }

    /// <summary>The registry key (what MewVG receives as the native handle).</summary>
    public nint Key { get; }

    /// <summary>A MewUI image over this source (the same object <c>CreateImageView(IExternalRasterSource)</c> returns).</summary>
    public IImage CreateImage()
    {
        return new Aprillz.MewUI.Rendering.MewVG.MewVGImage(this);
    }

    /// <inheritdoc />
    public int PixelWidth { get; }
    /// <inheritdoc />
    public int PixelHeight { get; }
    /// <inheritdoc />
    public int Version => 0;
    /// <inheritdoc />
    public int StrideBytes => PixelWidth * 4;
    /// <inheritdoc />
    public bool IsPremultiplied { get; }
    /// <inheritdoc />
    public bool HasAlpha { get; }
    /// <inheritdoc />
    public LockMode LockMode => LockMode.Readback;
    /// <inheritdoc />
    public RenderPixelFormat Format => IsPremultiplied ? RenderPixelFormat.Rgba8888Premultiplied : RenderPixelFormat.Rgba8888;
    /// <inheritdoc />
    public BitmapAlphaMode AlphaMode => BitmapAlphaMode.Ignore;
    /// <inheritdoc />
    public bool YFlipped => false;
    /// <inheritdoc />
    public SurfaceCapabilities Capabilities => SurfaceCapabilities.GpuSampleable;
    /// <inheritdoc />
    public IReadOnlyList<ExternalRasterPlane> Planes => _planes;

    /// <inheritdoc />
    public nint GetTextureHandle()
    {
        return _disposed ? 0 : Key;
    }

    /// <inheritdoc />
    public IExternalRasterLease Acquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new Lease(this);
    }

    /// <summary>CPU fallback only reached when the GPU wrap failed (a graphics context that is not this backend's): an opaque black
    /// buffer of the right size, never a stall on a GPU readback.</summary>
    public PixelBufferLock Lock()
    {
        _cpuFallback ??= CreateOpaqueBlack(PixelWidth, PixelHeight);
        return new PixelBufferLock(_cpuFallback, PixelWidth, PixelHeight, StrideBytes, Version, null, null);
    }

    private static byte[] CreateOpaqueBlack(int w, int h)
    {
        byte[] b = new byte[w * h * 4];
        for (int i = 3; i < b.Length; i += 4)
        {
            b[i] = byte.MaxValue;
        }

        return b;
    }

    /// <summary>Unregisters the key. The texture itself is the producer's to free (deferred past frames in flight).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ExternalTextures.Unregister(Key);
    }

    private sealed class Lease : IExternalRasterLease
    {
        private readonly GpuTextureSource _src;
        public Lease(GpuTextureSource src) { _src = src; }
        public int PixelWidth => _src.PixelWidth;
        public int PixelHeight => _src.PixelHeight;
        public bool YFlipped => false;
        public nint NativeHandle => _src.GetTextureHandle();
        public nint NativeAlternateHandle => 0;
        public void Dispose() { }
    }
}

/// <summary>Adapts a foreign <see cref="IExternalRasterSource"/> whose lease handle is a <see cref="ExternalTextures"/>
/// key into the pixel + GPU source pair MewVGImage consumes.</summary>
internal sealed class ExternalRasterAdapter : IPixelBufferSource, IGpuTextureSource
{
    private readonly IExternalRasterSource _source;
    private readonly nint _key;
    private byte[]? _cpuFallback;

    public ExternalRasterAdapter(IExternalRasterSource source, nint key)
    {
        _source = source;
        _key = key;
    }

    public int PixelWidth => _source.PixelWidth;
    public int PixelHeight => _source.PixelHeight;
    public int Version => _source.Version;
    public int StrideBytes => PixelWidth * 4;
    public bool IsPremultiplied => _source.Format is RenderPixelFormat.Rgba8888Premultiplied or RenderPixelFormat.Bgra8888Premultiplied;
    public bool HasAlpha => _source.AlphaMode != BitmapAlphaMode.Ignore;
    public LockMode LockMode => LockMode.Readback;
    public nint GetTextureHandle()
    {
        return _key;
    }

    public PixelBufferLock Lock()
    {
        _cpuFallback ??= new byte[PixelWidth * PixelHeight * 4];
        return new PixelBufferLock(_cpuFallback, PixelWidth, PixelHeight, StrideBytes, Version, null, null);
    }
}
