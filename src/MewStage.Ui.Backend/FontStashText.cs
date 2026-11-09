// FontStashSharp text for the MewUI backend. MewVG has no glyph rasterizer, so FontStashSharp is plugged in under
// MewUI's backend text contract:
//   * FontAtlasPages          : ITexture2DManager  - atlas pages as UiMewVGContext images.
//   * FontStashQuadSink       : IFontStashRenderer2 - each glyph quad becomes two triangles in the ui renderer
//     (ui.hlsl type 3), transformed by the context's current transform and clipped by its scissor rectangle.
//   * FontStashTextRenderer   - per SURFACE: one FontStashSharp system per face (each owns its atlas pages).
//   * TextMeasure             - per FACTORY: measures with a face's measure-only system, so the managed text engine
//     can lay text out without a surface. Measuring and drawing use the same face bytes, size and FontStashSharp
//     layout rule (integer advances at the raster pixel size), so a measured run draws exactly that wide.

using System.Drawing;
using System.Numerics;

using Aprillz.MewVG;

using FontStashSharp;
using FontStashSharp.Interfaces;

namespace MewStage.Ui.Backend;

/// <summary>Boxed atlas-page handle: the MewVG image id plus its dimensions.</summary>
public sealed class FontAtlasPage
{
    /// <summary>The MewVG image id of the page.</summary>
    public int Image { get; init; }

    /// <summary>Page width in texels.</summary>
    public int Width { get; init; }

    /// <summary>Page height in texels.</summary>
    public int Height { get; init; }
}

/// <summary><see cref="ITexture2DManager"/> over <see cref="UiMewVGContext"/> images (RGBA, straight alpha).</summary>
public sealed class FontAtlasPages : ITexture2DManager
{
    private readonly UiMewVGContext _vg;

    // FontStashSharp allocates a new atlas page when the current one fills and keeps sampling every earlier page,
    // so pages are never freed while the system lives (ITexture2DManager has no delete). The count is a leak signal.
    /// <summary>A desktop UI needs one or two pages; more than this is worth investigating (logged).</summary>
    private const int PageWarnThreshold = 8;

    /// <summary>Creates the page manager over <paramref name="vg"/>.</summary>
    /// <param name="vg">The context whose images hold the pages.</param>
    public FontAtlasPages(UiMewVGContext vg)
    {
        _vg = vg;
    }

    /// <summary>Atlas pages created (diagnostic).</summary>
    public int PageCount { get; private set; }

    /// <inheritdoc />
    public object CreateTexture(int width, int height)
    {
        byte[] zero = new byte[width * height * 4];
        int image = _vg.CreateImageRGBA(width, height, MewVGImageFlags.None, zero);
        PageCount++;
        if (PageCount == PageWarnThreshold || (PageCount > PageWarnThreshold && (PageCount % PageWarnThreshold) == 0))
        {
            Console.Error.WriteLine($"[ui] font atlas page count = {PageCount} ({width}x{height}) - live pages are not freed; a high count is an atlas-churn leak.");
        }

        return new FontAtlasPage { Image = image, Width = width, Height = height };
    }

    /// <inheritdoc />
    public Point GetTextureSize(object texture)
    {
        var t = (FontAtlasPage)texture;
        return new Point(t.Width, t.Height);
    }

    /// <inheritdoc />
    public void SetTextureData(object texture, Rectangle bounds, byte[] data)
    {
        var t = (FontAtlasPage)texture;
        _vg.UpdateImageRegion(t.Image, bounds.X, bounds.Y, bounds.Width, bounds.Height, data);
    }
}

/// <summary>
/// <see cref="IFontStashRenderer2"/> that emits glyph quads into the ui sink. The draw state (transform, scissor,
/// alpha) is set by <see cref="FontStashTextRenderer.DrawText"/> for the duration of one run.
/// </summary>
public sealed class FontStashQuadSink : IFontStashRenderer2
{
    private readonly UiMewVGContext _vg;
    private readonly FontAtlasPages _textures;

    internal Matrix3x2 Transform = Matrix3x2.Identity;
    internal NanoVgRenderer.TextClip Clip;
    internal float Alpha = 1f;

    /// <summary>Creates the sink over <paramref name="vg"/>.</summary>
    /// <param name="vg">The context the glyph triangles go to.</param>
    public FontStashQuadSink(UiMewVGContext vg)
    {
        _vg = vg;
        _textures = new FontAtlasPages(vg);
    }

    /// <inheritdoc />
    public ITexture2DManager TextureManager => _textures;

    /// <inheritdoc />
    public void DrawQuad(
        object texture,
        ref VertexPositionColorTexture topLeft,
        ref VertexPositionColorTexture topRight,
        ref VertexPositionColorTexture bottomLeft,
        ref VertexPositionColorTexture bottomRight)
    {
        var t = (FontAtlasPage)texture;
        FSColor c = topLeft.Color;
        var col = new MewVGColor(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f * Alpha);

        Span<NVGvertex> tris =
        [
            V(topLeft),
            V(topRight),
            V(bottomLeft),
            V(topRight),
            V(bottomRight),
            V(bottomLeft),
        ];

        _vg.Renderer.DrawTextTriangles(tris, t.Image, col, in Clip);
    }

    private NVGvertex V(in VertexPositionColorTexture v)
    {
        var p = Vector2.Transform(new Vector2(v.Position.X, v.Position.Y), Transform);
        return new NVGvertex(p.X, p.Y, v.TextureCoordinate.X, v.TextureCoordinate.Y);
    }
}

/// <summary>Per-surface glyph rasterization: one FontStashSharp system (and atlas) per face.</summary>
public sealed class FontStashTextRenderer : IDisposable
{
    private readonly FontStashQuadSink _renderer;
    private readonly Dictionary<FontFace, FontSystem> _systems = [];

    /// <summary>Creates the per-surface text renderer over <paramref name="vg"/>.</summary>
    /// <param name="vg">The surface's MewVG context.</param>
    public FontStashTextRenderer(UiMewVGContext vg)
    {
        _renderer = new FontStashQuadSink(vg);
    }

    /// <summary>Atlas pages created on this surface (diagnostic).</summary>
    public int AtlasPageCount => ((FontAtlasPages)_renderer.TextureManager).PageCount;

    private FontSystem SystemFor(FontFace face)
    {
        if (!_systems.TryGetValue(face, out FontSystem? fs))
        {
            fs = new FontSystem();
            fs.AddFont(face.Ttf);
            _systems[face] = fs;
        }

        return fs;
    }

    /// <summary>Draw one run with its top-left at <paramref name="originDip"/> (DIP, before
    /// <paramref name="transform"/>). Glyphs rasterize at the font's device pixel size and are scaled back to DIPs.</summary>
    internal void DrawText(string text, UiFont font, Vector2 originDip, MewVGColor color, in Matrix3x2 transform, in NanoVgRenderer.TextClip clip, float alpha)
    {
        if (string.IsNullOrEmpty(text) || color.A <= 0f)
        {
            return;
        }

        DynamicSpriteFont dynamicFont = SystemFor(font.Face).GetFont(font.RasterPixelSize);
        float inv = (float)(1.0 / font.DpiScale);
        _renderer.Transform = transform;
        _renderer.Clip = clip;
        _renderer.Alpha = alpha;
        var fsColor = new FSColor((byte)(color.R * 255), (byte)(color.G * 255), (byte)(color.B * 255), (byte)(color.A * 255));
        dynamicFont.DrawText(_renderer, text, originDip, fsColor, 0f, Vector2.Zero, new Vector2(inv, inv), 0f, 0f, 0f, TextStyle.None, FontSystemEffect.None, 0);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (FontSystem fs in _systems.Values)
        {
            fs.Dispose();
        }

        _systems.Clear();
    }
}

/// <summary>Measurement on a face's measure-only FontStashSharp system (no surface, no atlas).</summary>
/// <remarks>FontStashSharp's <see cref="Glyph.Bounds"/> is the glyph's INK box: its X is the pen position plus the
/// glyph's left-side bearing (render offset). Advances are PEN positions, so each glyph's pen is its Bounds.X minus
/// that bearing, which is read once per (font, code point) by laying the glyph out alone at pen 0. (Using Bounds.X as
/// the pen made every measured run too wide by its last glyph's bearing, which opened gaps in per-character tracked
/// text and misplaced runs that the text engine positions from prefix advances.)</remarks>
internal static class TextMeasure
{
    /// <summary>Single-line advance width and line height of <paramref name="text"/> in DIPs. Line breaks
    /// start new lines (the managed engine hands runs without them; MeasureText may get them).</summary>
    public static Aprillz.MewUI.Size Measure(ReadOnlySpan<char> text, UiFont font)
    {
        double lineHeight = font.Ascent + font.Descent;
        if (text.IsEmpty)
        {
            return new Aprillz.MewUI.Size(0, lineHeight);
        }

        double widest = 0;
        int lines = 0, start = 0;
        for (int i = 0; i <= text.Length; i++)
        {
            if (i != text.Length && text[i] != '\n')
            {
                continue;
            }

            int end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            widest = Math.Max(widest, LineAdvance(text[start..end], font));
            lines++;
            start = i + 1;
        }

        return new Aprillz.MewUI.Size(widest, lineHeight * Math.Max(1, lines));
    }

    private static double LineAdvance(ReadOnlySpan<char> line, UiFont font)
    {
        if (line.IsEmpty)
        {
            return 0;
        }

        DynamicSpriteFont dynamicFont = Font(font);
        List<Glyph> glyphs = Glyphs(line, dynamicFont, out _);
        if (glyphs.Count == 0)
        {
            return 0;
        }

        Glyph last = glyphs[^1];
        return (Pen(dynamicFont, last) + last.XAdvance) / font.DpiScale;
    }

    /// <summary>The pen position after every UTF-16 code unit, in DIPs (both halves of a surrogate pair report the
    /// position after the pair). Kerning is in, because it moves the next glyph's pen.</summary>
    public static void FillPrefixAdvances(ReadOnlySpan<char> text, UiFont font, Span<double> destination)
    {
        if (text.IsEmpty)
        {
            return;
        }

        DynamicSpriteFont dynamicFont = Font(font);
        List<Glyph> glyphs = Glyphs(text, dynamicFont, out string s);
        int unit = 0;
        for (int g = 0; g < glyphs.Count && unit < s.Length; g++)
        {
            // The pen after glyph g: the next glyph's pen (kerning included), else this glyph's pen + advance.
            double after = g + 1 < glyphs.Count ? Pen(dynamicFont, glyphs[g + 1]) : Pen(dynamicFont, glyphs[g]) + glyphs[g].XAdvance;
            after /= font.DpiScale;
            int units = char.IsHighSurrogate(s[unit]) && unit + 1 < s.Length && char.IsLowSurrogate(s[unit + 1]) ? 2 : 1;
            for (int k = 0; k < units; k++)
            {
                destination[unit++] = after;
            }
        }
        // A code unit FontStashSharp produced no glyph for (it skips nothing in practice) keeps the last position.
        for (double last = unit > 0 ? destination[unit - 1] : 0; unit < s.Length; unit++)
        {
            destination[unit] = last;
        }
    }

    private static DynamicSpriteFont Font(UiFont font)
    {
        return font.Face.MeasureSystem.GetFont(font.RasterPixelSize);
    }

    private static List<Glyph> Glyphs(ReadOnlySpan<char> text, DynamicSpriteFont dynamicFont, out string s)
    {
        s = UiTextIntern.Get(text);
        return dynamicFont.GetGlyphs(s, Vector2.Zero, Vector2.Zero, null, 0f, 0f, FontSystemEffect.None, 0);
    }

    // (font, code point) -> the glyph's left-side bearing in raster pixels (Bounds.X when laid out alone at pen 0).
    private static readonly Dictionary<(DynamicSpriteFont Font, int Codepoint), int> Bearings = [];

    /// <summary>The pen position of a laid-out glyph: its ink X minus its bearing.</summary>
    private static int Pen(DynamicSpriteFont font, in Glyph glyph)
    {
        int bearing;
        lock (Bearings)
        {
            if (!Bearings.TryGetValue((font, glyph.Codepoint), out bearing))
            {
                List<Glyph> alone = font.GetGlyphs(char.ConvertFromUtf32(glyph.Codepoint), Vector2.Zero, Vector2.Zero, null, 0f, 0f, FontSystemEffect.None, 0);
                bearing = alone.Count > 0 ? alone[0].Bounds.X : 0;
                Bearings[(font, glyph.Codepoint)] = bearing;
            }
        }

        return glyph.Bounds.X - bearing;
    }
}
