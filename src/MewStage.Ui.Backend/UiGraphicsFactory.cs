// UiGraphicsFactory: the MewUI IGraphicsFactory for the MewStage GPU stack. IGraphicsFactory extends IRenderDevice, so
// the factory IS the device from MewUI's point of view; this class composes a UiRenderDevice for the surface / context /
// image-view / readback half and adds the factory half:
//   * fonts: a FontCollection (the faces; CSS face matching) and interned UiFont instances;
//   * the backend text contract (ITextBackendFactory): a measurement context with REAL advances, so MewUI's managed
//     text engine lays text out exactly as FontStashSharp draws it, without a GPU surface.
//
// Bootstrap: Register(...) makes this factory Application.DefaultGraphicsFactory (repeatable: hosts are rebuilt in
// tests and on a device swap), so an embedded Window resolves it without Application.Run.

using System.IO;

using Aprillz.MewUI;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Resources;
using Aprillz.MewUI.Text;

using MewStage.Graphics;

namespace MewStage.Ui.Backend;

/// <summary>The MewStage MewUI graphics factory (IGraphicsFactory == IRenderDevice). Backend id "MewStage".</summary>
public sealed class UiGraphicsFactory : IGraphicsFactory, ITextBackendFactory
{
    /// <summary>The identifier MewUI reports for this backend.</summary>
    public const string BackendIdentifier = "MewStage";
    private bool _disposed;

    /// <inheritdoc />
    public string Backend => BackendIdentifier;

    /// <summary>The faces this factory resolves fonts from. Starts with the built-in default (Roboto Regular);
    /// the application adds its own before the first layout.</summary>
    public FontCollection Fonts { get; } = new();

    /// <summary>Creates a factory over <paramref name="device"/> with the built-in default face registered.</summary>
    /// <param name="device">The application's GPU device.</param>
    /// <param name="uiVs">The cooked ui.hlsl vertex shader for this device's backend.</param>
    /// <param name="uiPs">The cooked ui.hlsl pixel shader.</param>
    /// <param name="flipClipY">True on DX12 / Metal (clip Y up), false on Vulkan.</param>
    /// <param name="framesInFlight">The scheduler's frame depth (sizes the per-frame upload rings).</param>
    public UiGraphicsFactory(IGraphicsDevice device, byte[] uiVs, byte[] uiPs, bool flipClipY = false, int framesInFlight = 2)
    {
        Fonts.Add(BuiltInDefaultFont(), isDefault: true);
        Device = new UiRenderDevice(device, uiVs, uiPs, flipClipY, framesInFlight);
    }

    /// <summary>Roboto Regular, embedded in this assembly (SIL Open Font License 1.1).</summary>
    public static byte[] BuiltInDefaultFont()
    {
        const string Name = "MewStage.Ui.Backend.Fonts.Roboto-Regular.ttf";
        using Stream s = typeof(UiGraphicsFactory).Assembly.GetManifestResourceStream(Name)
            ?? throw new InvalidOperationException("embedded resource missing: " + Name);
        byte[] bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Make this factory MewUI's default graphics backend (Application.DefaultGraphicsFactory).
    /// Repeatable within a process; call at host init before constructing windows.</summary>
    public static UiGraphicsFactory Register(IGraphicsDevice device, byte[] uiVs, byte[] uiPs, bool flipClipY = false, int framesInFlight = 2)
    {
        var factory = new UiGraphicsFactory(device, uiVs, uiPs, flipClipY, framesInFlight);
        Application.DefaultGraphicsFactory = factory;
        return factory;
    }

    // ---- fonts ----

    // Fonts are interned: a font is an immutable description (face / size / dpi / style; Dispose is a no-op), and
    // controls create theirs while rendering, so equal requests share one instance. The lock makes a
    // stray cross-thread measure safe; the UI renders on one thread, so it is uncontended.
    private readonly Dictionary<FontKey, UiFont> _fonts = [];

    private readonly record struct FontKey(string Family, double Size, uint Dpi, FontWeight Weight, bool Italic, bool Underline, bool Strikethrough);

    /// <inheritdoc />
    public IFont CreateFont(string family, double size, FontWeight weight = FontWeight.Normal,
        bool italic = false, bool underline = false, bool strikethrough = false)
    {
        return CreateFont(family, size, 96, weight, italic, underline, strikethrough);
    }

    /// <inheritdoc />
    public IFont CreateFont(string family, double size, uint dpi, FontWeight weight = FontWeight.Normal,
        bool italic = false, bool underline = false, bool strikethrough = false)
    {
        var key = new FontKey(family ?? "", size, dpi, weight, italic, underline, strikethrough);
        lock (_fonts)
        {
            if (!_fonts.TryGetValue(key, out UiFont? font))
            {
                FontFace face = Fonts.Resolve(family, weight, italic)
                    ?? throw new InvalidOperationException("no font face is registered with the MewStage graphics factory");
                _fonts[key] = font = new UiFont(face, family ?? face.Family, size, dpi, weight, italic, underline, strikethrough);
            }

            return font;
        }
    }

    // ---- the backend text contract ----

    ITextBackendMeasurementContext ITextBackendFactory.CreateTextMeasurementContext(uint dpi)
    {
        return new UiMeasurementContext(dpi);
    }

    // Images decode through MewUI's own ImageSource (GraphicsFactoryImageExtensions.CreateImageFromBytes / FromFile),
    // which hands the decoded pixels to CreateImageView(IPixelBufferSource) below.

    // ---- IRenderDevice (delegated to the composed UiRenderDevice) ----

    /// <inheritdoc />
    public IRenderSurface CreateSurface(RenderSurfaceDescriptor descriptor)
    {
        return Device.CreateSurface(descriptor);
    }

    /// <inheritdoc />
    public IGraphicsContext CreateContext(IRenderTarget target)
    {
        return Device.CreateContext(target);
    }

    /// <inheritdoc />
    public IGraphicsContext CreateContext(IRenderSurface surface)
    {
        return Device.CreateContext(surface);
    }

    /// <inheritdoc />
    public IImage CreateImageView(IRenderSurface surface)
    {
        return Device.CreateImageView(surface);
    }

    /// <inheritdoc />
    public IImage CreateImageView(IPixelBufferSource source)
    {
        return Device.CreateImageView(source);
    }

    /// <inheritdoc />
    public IImage CreateImageView(IExternalRasterSource source)
    {
        return Device.CreateImageView(source);
    }

    /// <inheritdoc />
    public bool TryReadPixels(IRenderSurface source, Span<byte> destination, int destinationStrideBytes)
    {
        return Device.TryReadPixels(source, destination, destinationStrideBytes);
    }

    /// <inheritdoc />
    public IRenderOperation RequestReadback(IRenderSurface source)
    {
        return Device.RequestReadback(source);
    }

    /// <inheritdoc />
    public IRenderOperation FlushAsyncWork()
    {
        return Device.FlushAsyncWork();
    }

    /// <inheritdoc />
    public IRenderResourceCache? ResourceCache => Device.ResourceCache;

    /// <summary>The composed render device (the host binds the frame on it for scratch renders).</summary>
    internal UiRenderDevice Device { get; }

    /// <summary>Not supported: the embedded host records GPU work inside the frame, so a cache cannot be rasterized on
    /// a worker thread. MewUI catches this, marks the control, and rasterizes it synchronously from then on
    /// (Image.VectorSource.cs: <c>_vectorAsyncUnsupported</c>).</summary>
    public IDisposable AcquireBackgroundRenderScope()
    {
        throw new NotSupportedException("the embedded MewUI host rasterizes caches inside the frame; off-thread rendering is not available");
    }
    /// <inheritdoc />
    public IRenderEffectDevice? Effects => Device.Effects;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Device.Dispose();
        Fonts.Dispose();
    }
}

/// <summary>
/// The measurement session MewUI's managed text engine lays text out with: FontStashSharp advances on the face's
/// measure-only system (no surface), prefix advances included, so wrapping, alignment, trimming and caret positions
/// agree with what the render context draws. (An estimate per character would misplace every caret.)
/// </summary>
internal sealed class UiMeasurementContext : MeasureGraphicsContextBase, ITextAdvanceSource
{
    public UiMeasurementContext(uint dpi)
    {
        DpiScale = dpi > 0 ? dpi / 96.0 : 1.0;
    }

    public override double DpiScale { get; }

    public override Size MeasureText(ReadOnlySpan<char> text, IFont font)
    {
        return Measure(text, font);
    }

    public override Size MeasureText(ReadOnlySpan<char> text, IFont font, double maxWidth)
    {
        return Measure(text, font);
    }

    internal static Size Measure(ReadOnlySpan<char> text, IFont font)
    {
        return font is UiFont f
                ? TextMeasure.Measure(text, f)
                : throw new ArgumentException($"font {font?.GetType().Name ?? "null"} was not created by the MewStage graphics factory", nameof(font));
    }

    double[] ITextAdvanceSource.GetUtf16PrefixAdvances(ReadOnlySpan<char> text, IFont font)
    {
        if (text.IsEmpty)
        {
            return [];
        }

        double[] advances = new double[text.Length];
        TextMeasure.FillPrefixAdvances(text, Font(font), advances);
        return advances;
    }

    bool ITextAdvanceSource.TryGetUtf16PrefixAdvances(ReadOnlySpan<char> text, IFont font, Span<double> destination)
    {
        if (text.IsEmpty)
        {
            return true;
        }

        if (destination.Length < text.Length)
        {
            return false;
        }

        TextMeasure.FillPrefixAdvances(text, Font(font), destination);
        return true;
    }

    private static UiFont Font(IFont font)
    {
        return font as UiFont ?? throw new ArgumentException($"font {font?.GetType().Name ?? "null"} was not created by the MewStage graphics factory", nameof(font));
    }
}
