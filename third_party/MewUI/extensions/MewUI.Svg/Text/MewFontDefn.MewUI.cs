using System.Diagnostics;

using Aprillz.MewUI;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;

namespace Svg;

internal sealed class MewFontDefn : IFontDefn
{
    private readonly IFont _font;
    private readonly IGlyphOutlineFont? _outlineFont;
    private readonly double _ppi;

    public double Size => _font.Size;
    public double SizeInPoints => _font.Size * 72.0 / _ppi;

    public MewFontDefn(IFont font, double ppi)
    {
        _font = font;
        _outlineFont = font as IGlyphOutlineFont;
        _ppi = ppi;
    }

    public void AddStringToPath(ISvgRenderer renderer, PathGeometry path, string text, Point location)
    {
        if (_outlineFont is null)
        {
            return;
        }

        // Each glyph is rendered at its own gmCellIncX-based advance (kerning-unaware),
        // but adjacent-character cursor positions must respect the kerning the layout
        // engine applied to the prefix. Earlier code used `cursor[i] = MeasureText(prefix
        // exclusive of char i)` which placed each char at the kerned end of the previous
        // prefix - but the previous glyph was drawn at its un-kerned own advance, so the
        // two diverged by the kerning amount. Visible as adjacent kerning pairs (e.g.
        // Arial 'Te') overlapping the next glyph by ~5 px at 48 px font.
        //
        // Correct cursor = MeasureText(prefix INCLUSIVE) − own advance of this glyph.
        // The inclusive prefix is the kerned right edge of the run up to and including
        // this glyph; subtracting the glyph's own advance yields its kerned start
        // position. Single-character MeasureText returns the glyph's own advance with
        // no kerning partner.
        var layout = GetLayout(renderer, text);
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var prefixInclusive = layout.GetCaretBounds(new CharacterHit(i + 1, 0)).X;
            var ownAdvance = GetLayout(renderer, ch.ToString()).MeasuredSize.Width;
            var cursor = new Point(location.X + prefixInclusive - ownAdvance, location.Y);
            _outlineFont.TryAppendGlyphOutline(path, ch, cursor, out _);
        }
    }

    public double Ascent(ISvgRenderer renderer) => _font.Ascent;

    public IList<Rect> MeasureCharacters(ISvgRenderer renderer, string text)
    {
        var results = new List<Rect>(text.Length);
        var layout = GetLayout(renderer, text);
        double previousWidth = 0;
        for (int i = 0; i < text.Length; i++)
        {
            double currentWidth = layout.GetCaretBounds(new CharacterHit(i + 1, 0)).X;
            var width = Math.Max(0, currentWidth - previousWidth);
            results.Add(new Rect(previousWidth, 0, width, Ascent(renderer)));
            previousWidth = currentWidth;
        }

        return results;
    }

    public Size MeasureString(ISvgRenderer renderer, string text)
    {
        var size = GetLayout(renderer, text).MeasuredSize;
        return new Size(size.Width, Ascent(renderer));
    }

    private ITextLayout GetLayout(ISvgRenderer renderer, string text)
    {
        var decoration = (_font.IsUnderline ? TextDecoration.Underline : TextDecoration.None) |
                         (_font.IsStrikethrough ? TextDecoration.Strikethrough : TextDecoration.None);
        return renderer.GraphicsFactory.TextEngine.GetOrCreateLayout(
            new TextLayoutRequest
            {
                Text = (text ?? string.Empty).AsMemory(),
                Dpi = (uint)Math.Clamp(Math.Round(_ppi), 1, uint.MaxValue),
                DefaultStyle = new TextRunStyle(
                    _font.Family,
                    _font.Size,
                    _font.Weight,
                    _font.IsItalic,
                    decoration)
            },
            TextLayoutCachePolicy.Content);
    }

    public void Dispose()
    {
        _font.Dispose();
    }

}
