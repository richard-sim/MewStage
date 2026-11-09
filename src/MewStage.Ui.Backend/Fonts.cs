// The backend's font system: one registry of font FACES per graphics factory, so text is measured without a GPU
// surface, and fonts carry the metrics the TTF declares.
//
//   * FontFace        one TTF: its family / weight / italic (read from the font's own name and OS/2 tables) and its
//                     vertical metrics as fractions of the em (hhea ascent / descent, OS/2 cap height).
//   * FontCollection  the faces a factory knows, and CSS-style face matching: CreateFont("Lora", 24, Bold, italic)
//                     finds Lora Bold Italic, the way a browser would with the same files. One face is the default
//                     for families nobody registered.
//   * UiFont          the MewUI IFont: a face at a size (DIP) and DPI. Ascent / Descent / CapHeight are the face's em
//                     fractions times the size: the real line box, not a typographic guess.
//
// Glyphs are rasterized by FontStashSharp. FontStashSharp sizes a font so that (ascent - descent) spans the requested
// pixel size (stbtt ScaleForPixelHeight), so a CSS-style em size S is requested as S * EmFactor, with EmFactor =
// (ascent - descent) / unitsPerEm. One font unit is then exactly S / unitsPerEm pixels, and FontStashSharp puts the
// baseline at ascent * S below the run top, the same place the managed text engine expects it (Ascent).

using System.Text;

using Aprillz.MewUI;
using Aprillz.MewUI.Rendering;

using FontStashSharp;

namespace MewStage.Ui.Backend;

/// <summary>One registered TTF face.</summary>
public sealed class FontFace
{
    internal FontFace(byte[] ttf, string family, int weight, bool italic, double ascentEm, double descentEm, double capHeightEm, int index)
    {
        Ttf = ttf;
        Family = family;
        Weight = weight;
        Italic = italic;
        AscentEm = ascentEm;
        DescentEm = descentEm;
        CapHeightEm = capHeightEm;
        Index = index;
    }

    /// <summary>The TTF bytes (shared by every FontStashSharp system that rasterizes this face).</summary>
    public byte[] Ttf { get; }
    /// <summary>Typographic family (name ID 16, else name ID 1), e.g. "Kanit", "Barlow Semi Condensed".</summary>
    public string Family { get; }
    /// <summary>OS/2 usWeightClass (100..900).</summary>
    public int Weight { get; }
    /// <summary>OS/2 fsSelection bit 0.</summary>
    public bool Italic { get; }
    /// <summary>hhea ascender / unitsPerEm.</summary>
    public double AscentEm { get; }
    /// <summary>-hhea descender / unitsPerEm (positive).</summary>
    public double DescentEm { get; }
    /// <summary>OS/2 sCapHeight / unitsPerEm (OS/2 version 2+), else the ascent (no better data in the font).</summary>
    public double CapHeightEm { get; }
    /// <summary>FontStashSharp pixel size per CSS em pixel: (ascent - descent) / unitsPerEm.</summary>
    public double EmFactor => AscentEm + DescentEm;
    /// <summary>Registration order (stable identity for per-surface rasterizers).</summary>
    public int Index { get; }

    private FontSystem? _measure;
    /// <summary>A FontStashSharp system used ONLY to measure (no atlas is ever created on it).</summary>
    internal FontSystem MeasureSystem
    {
        get
        {
            if (_measure is null)
            {
                var fs = new FontSystem();
                fs.AddFont(Ttf);
                _measure = fs;
            }

            return _measure;
        }
    }

    internal void DisposeMeasure()
    {
        _measure?.Dispose();
        _measure = null;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"{Family} {Weight}{(Italic ? "i" : "")}";
    }
}

/// <summary>The faces a graphics factory knows, with CSS font matching. Thread-safe.</summary>
public sealed class FontCollection : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<FontFace> _faces = [];
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private FontFace? _default;

    /// <summary>Every registered face, in registration order.</summary>
    public IReadOnlyList<FontFace> Faces
    {
        get
        {
            lock (_gate)
            {
                return _faces.ToArray();
            }
        }
    }

    /// <summary>The face used for a family nobody registered (the first face registered as default).</summary>
    public FontFace? Default
    {
        get
        {
            lock (_gate)
            {
                return _default;
            }
        }
    }

    /// <summary>Register a TTF. Its family, weight and style come from the font's own tables.
    /// <paramref name="isDefault"/>: also the fallback for unknown families (the first such face wins).</summary>
    public FontFace Add(byte[] ttf, bool isDefault = false)
    {
        ArgumentNullException.ThrowIfNull(ttf);
        var info = TrueTypeInfo.Read(ttf);
        lock (_gate)
        {
            var face = new FontFace(ttf, info.Family, info.Weight, info.Italic, info.AscentEm, info.DescentEm, info.CapHeightEm, _faces.Count);
            _faces.Add(face);
            if (isDefault && _default is null)
            {
                _default = face;
            }

            return face;
        }
    }

    /// <summary>Let <paramref name="alias"/> name <paramref name="family"/> (e.g. a short theme name).</summary>
    public void AddAlias(string alias, string family)
    {
        ArgumentException.ThrowIfNullOrEmpty(alias);
        ArgumentException.ThrowIfNullOrEmpty(family);
        lock (_gate)
        {
            _aliases[alias] = family;
        }
    }

    /// <summary>The face a browser would pick for (family, weight, italic) among the registered ones (CSS Fonts 3
    /// section 5.2 font matching): an exact family match, then the style (italic before roman when italic is
    /// asked, and the reverse), then the weight - for 400 and 500 the other of the two, then lighter, then
    /// heavier; below 400 lighter then heavier; above 500 heavier then lighter. An unknown family resolves in the
    /// default face's family; no faces at all returns null.</summary>
    public FontFace? Resolve(string? family, FontWeight weight, bool italic)
    {
        lock (_gate)
        {
            if (_faces.Count == 0)
            {
                return null;
            }

            string name = family ?? "";
            if (_aliases.TryGetValue(name, out string? aliased))
            {
                name = aliased;
            }

            List<FontFace>? set = null;
            foreach (FontFace f in _faces)
            {
                if (string.Equals(f.Family, name, StringComparison.OrdinalIgnoreCase))
                {
                    (set ??= []).Add(f);
                }
            }

            if (set is null)
            {
                FontFace fallback = _default ?? _faces[0];
                set = [];
                foreach (FontFace f in _faces)
                {
                    if (string.Equals(f.Family, fallback.Family, StringComparison.Ordinal))
                    {
                        set.Add(f);
                    }
                }
            }

            // Style: keep the faces of the wanted style when there are any, else the others.
            bool any = false;
            foreach (FontFace f in set)
            {
                if (f.Italic == italic)
                {
                    any = true;
                    break;
                }
            }

            if (any)
            {
                set.RemoveAll(f => f.Italic != italic);
            }

            int want = (int)weight;
            FontFace? best = null;
            int bestRank = int.MaxValue;
            foreach (FontFace f in set)
            {
                int rank = WeightRank(want, f.Weight);
                if (rank < bestRank)
                {
                    bestRank = rank;
                    best = f;
                }
            }

            return best;
        }
    }

    /// <summary>Lower is better: the CSS weight-matching order as one integer.</summary>
    internal static int WeightRank(int want, int have)
    {
        if (have == want)
        {
            return 0;
        }

        const int Tier = 10_000;   // separates the preference tiers; weights span 1..1000
        if (want is >= 400 and <= 500)
        {
            // 400 and 500: first the other of the pair (between want and 500), then lighter (nearest first), then heavier.
            if (have > want && have <= 500)
            {
                return have - want;
            }

            if (have < want)
            {
                return Tier + (want - have);
            }

            return (2 * Tier) + (have - want);
        }

        if (want < 400)
        {
            return have < want ? Tier + (want - have) : (2 * Tier) + (have - want);
        }

        return have > want ? Tier + (have - want) : (2 * Tier) + (want - have);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (FontFace f in _faces)
            {
                f.DisposeMeasure();
            }
        }
    }
}

/// <summary>A MewUI <see cref="IFont"/> over a registered face: real vertical metrics, FontStashSharp glyphs.</summary>
internal sealed class UiFont : FontBase
{
    public UiFont(FontFace face, string requestedFamily, double size, uint dpi, FontWeight weight, bool italic, bool underline, bool strikethrough)
        : base(requestedFamily, size, weight, italic, underline, strikethrough)
    {
        Face = face;
        Dpi = dpi == 0 ? 96 : dpi;
        DpiScale = Dpi / 96.0;
        Ascent = face.AscentEm * size;
        Descent = face.DescentEm * size;
        InternalLeading = 0.0;
        CapHeight = face.CapHeightEm * size;
        // Rasterize at DEVICE pixels; quads are scaled back to DIPs at draw time (FontStashTextRenderer).
        RasterPixelSize = (float)(size * DpiScale * face.EmFactor);
    }

    public FontFace Face { get; }
    public uint Dpi { get; }
    /// <summary>Device pixels per DIP this font rasterizes for.</summary>
    public double DpiScale { get; }
    /// <summary>The size handed to FontStashSharp's GetFont (device pixels, em-factor applied).</summary>
    public float RasterPixelSize { get; }
}

/// <summary>Minimal TrueType table reader: name, OS/2, hhea, head. Malformed input throws (registration fails loudly).</summary>
internal readonly record struct TrueTypeInfo(string Family, int Weight, bool Italic, double AscentEm, double DescentEm, double CapHeightEm)
{
    public static TrueTypeInfo Read(byte[] d)
    {
        int numTables = U16(d, 4);
        int head = -1, hhea = -1, os2 = -1, name = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + (i * 16);
            string tag = Encoding.ASCII.GetString(d, rec, 4);
            int off = (int)U32(d, rec + 8);
            switch (tag)
            {
                case "head":
                    head = off;
                    break;
                case "hhea":
                    hhea = off;
                    break;
                case "OS/2":
                    os2 = off;
                    break;
                case "name":
                    name = off;
                    break;
            }
        }

        if (head < 0 || hhea < 0)
        {
            throw new FormatException("not a TrueType font (no head/hhea table)");
        }

        double upm = U16(d, head + 18);
        if (upm <= 0)
        {
            throw new FormatException("TrueType unitsPerEm is 0");
        }

        double asc = S16(d, hhea + 4) / upm, desc = -S16(d, hhea + 6) / upm;

        int weight = 400;
        bool italic = false;
        double cap = asc;
        if (os2 >= 0)
        {
            int version = U16(d, os2);
            weight = U16(d, os2 + 4);
            italic = (U16(d, os2 + 62) & 1) != 0;   // fsSelection bit 0 = ITALIC
            if (version >= 2)
            {
                cap = S16(d, os2 + 88) / upm;   // sCapHeight
            }
        }

        string family = name >= 0 ? (NameRecord(d, name, 16) ?? NameRecord(d, name, 1) ?? "") : "";
        return new TrueTypeInfo(family, weight, italic, asc, desc, cap);
    }

    // Windows platform (3), Unicode BMP (1), US English (0x409), UTF-16BE - the record every TTF carries.
    private static string? NameRecord(byte[] d, int table, int nameId)
    {
        int count = U16(d, table + 2), strings = table + U16(d, table + 4);
        for (int i = 0; i < count; i++)
        {
            int r = table + 6 + (i * 12);
            if (U16(d, r) == 3 && U16(d, r + 2) == 1 && U16(d, r + 4) == 0x409 && U16(d, r + 6) == nameId)
            {
                return Encoding.BigEndianUnicode.GetString(d, strings + U16(d, r + 10), U16(d, r + 8));
            }
        }

        return null;
    }

    private static int U16(byte[] d, int o)
    {
        return (d[o] << 8) | d[o + 1];
    }

    private static short S16(byte[] d, int o)
    {
        return (short)((d[o] << 8) | d[o + 1]);
    }

    private static uint U32(byte[] d, int o)
    {
        return ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
    }
}
