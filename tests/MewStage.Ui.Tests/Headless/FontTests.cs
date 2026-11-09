using System;
using System.Collections.Generic;
using System.Numerics;

using Aprillz.MewUI;
using Aprillz.MewUI.Rendering;

using FontStashSharp;
using FontStashSharp.Interfaces;

using MewStage.Rendering.Backends.Headless;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Headless;

/// <summary>
/// The backend's fonts: faces resolve by (family, weight, italic) the way a browser matches CSS fonts, vertical
/// metrics come from the TTF's own tables (not typographic ratios), a measured run is as wide as the run FontStashSharp
/// draws, and the factory interns equal font requests.
/// </summary>
public sealed class FontTests
{
    private static FontCollection LoraCollection()
    {
        var fonts = new FontCollection();
        fonts.Add(UiGraphicsFactory.BuiltInDefaultFont(), isDefault: true);
        fonts.Add(TestAssets.Lora("Regular"));
        fonts.Add(TestAssets.Lora("Bold"));
        fonts.Add(TestAssets.Lora("Italic"));
        fonts.Add(TestAssets.Lora("BoldItalic"));
        return fonts;
    }

    [Fact]
    public void Fonts_ReadFamilyWeightAndStyle_FromTheTables()
    {
        using FontCollection fonts = LoraCollection();
        Assert.Equal(5, fonts.Faces.Count);
        FontFace roboto = fonts.Faces[0];
        Assert.Equal("Roboto", roboto.Family);
        Assert.Equal(400, roboto.Weight);
        Assert.False(roboto.Italic);

        FontFace boldItalic = fonts.Faces[4];
        Assert.Equal("Lora", boldItalic.Family);
        Assert.Equal(700, boldItalic.Weight);
        Assert.True(boldItalic.Italic);
    }

    [Fact]
    public void Fonts_ResolveFamilyWeightItalic_ToFace()
    {
        using FontCollection fonts = LoraCollection();
        FontFace? boldItalic = fonts.Resolve("Lora", FontWeight.Bold, italic: true);
        Assert.NotNull(boldItalic);
        Assert.Equal(700, boldItalic.Weight);
        Assert.True(boldItalic.Italic);

        FontFace? regular = fonts.Resolve("Lora", FontWeight.Normal, italic: false);
        Assert.NotNull(regular);
        Assert.Equal(400, regular.Weight);
        Assert.False(regular.Italic);

        // A weight nobody registered takes the nearest by the CSS rule: 500 asks 400 before 700.
        FontFace? medium = fonts.Resolve("Lora", FontWeight.Medium, italic: false);
        Assert.NotNull(medium);
        Assert.Equal(400, medium.Weight);

        // An italic nobody registered for the family falls back to the roman of the right weight.
        FontFace? robotoItalic = fonts.Resolve("Roboto", FontWeight.Normal, italic: true);
        Assert.NotNull(robotoItalic);
        Assert.Equal("Roboto", robotoItalic.Family);
        Assert.False(robotoItalic.Italic);

        // An unknown family resolves in the default face's family.
        FontFace? unknown = fonts.Resolve("No Such Family", FontWeight.Bold, italic: false);
        Assert.NotNull(unknown);
        Assert.Equal("Roboto", unknown.Family);
    }

    [Fact]
    public void Fonts_WeightRank_FollowsTheCssOrder()
    {
        // 400 and 500: the other of the pair first, then lighter, then heavier.
        Assert.True(FontCollection.WeightRank(400, 500) < FontCollection.WeightRank(400, 300));
        Assert.True(FontCollection.WeightRank(400, 300) < FontCollection.WeightRank(400, 700));
        Assert.True(FontCollection.WeightRank(500, 400) < FontCollection.WeightRank(500, 300));

        // Below 400: lighter first. Above 500: heavier first.
        Assert.True(FontCollection.WeightRank(300, 200) < FontCollection.WeightRank(300, 400));
        Assert.True(FontCollection.WeightRank(700, 900) < FontCollection.WeightRank(700, 400));
        Assert.Equal(0, FontCollection.WeightRank(700, 700));
    }

    [Fact]
    public void Fonts_Metrics_ComeFromTables_NotRatios()
    {
        using var dev = new HeadlessDevice();
        using var factory = new UiGraphicsFactory(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        factory.Fonts.Add(TestAssets.Lora("Regular"));

        IFont roboto = factory.CreateFont("Roboto", 100);
        IFont lora = factory.CreateFont("Lora", 100);

        // Roboto's hhea ascender is 1900 / 2048 em and its descender 500 / 2048 em; Lora's are 1006 / 1000 and 274 / 1000.
        // Neither is the 0.80 / 0.20 guess, and the two faces differ, which a ratio could never produce.
        Assert.Equal(1900.0 / 2048.0 * 100, roboto.Ascent, 2);
        Assert.Equal(500.0 / 2048.0 * 100, roboto.Descent, 2);
        Assert.Equal(100.6, lora.Ascent, 2);
        Assert.Equal(27.4, lora.Descent, 2);
        Assert.NotEqual(roboto.Ascent, lora.Ascent);
        Assert.True(roboto.CapHeight is > 60 and < 80, "cap height comes from OS/2 sCapHeight");
    }

    [Fact]
    public void Fonts_AreInterned_PerDescription()
    {
        using var dev = new HeadlessDevice();
        using var factory = new UiGraphicsFactory(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        IFont a = factory.CreateFont("Roboto", 14, FontWeight.Normal);
        IFont b = factory.CreateFont("Roboto", 14, FontWeight.Normal);
        IFont c = factory.CreateFont("Roboto", 14, FontWeight.Bold);
        Assert.Same(a, b);
        Assert.NotSame(a, c);
    }

    [Fact]
    public void Text_MeasuredWidth_EqualsDrawnWidth()
    {
        using var dev = new HeadlessDevice();
        using var factory = new UiGraphicsFactory(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        var font = (UiFont)factory.CreateFont("Roboto", 24, FontWeight.Normal);
        const string Text = "Hello, MewStage";

        Size measured = TextMeasure.Measure(Text, font);
        double[] prefix = new double[Text.Length];
        TextMeasure.FillPrefixAdvances(Text, font, prefix);
        Assert.Equal(measured.Width, prefix[^1], 6);           // the last prefix advance IS the width
        for (int i = 1; i < prefix.Length; i++)
        {
            Assert.True(prefix[i] >= prefix[i - 1], "prefix advances are monotonic");
        }

        // Draw the same run with FontStashSharp at the same raster size and collect the quads; the pen after the last
        // glyph (its quad's left edge minus its bearing, plus its advance) is where the measure says the run ends.
        var fs = new FontSystem();
        fs.AddFont(font.Face.Ttf);
        DynamicSpriteFont dyn = fs.GetFont(font.RasterPixelSize);
        var sink = new QuadCollector();
        dyn.DrawText(sink, Text, Vector2.Zero, FSColor.White);
        Assert.True(sink.Quads.Count >= Text.Replace(" ", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Length - 1, "glyph quads were drawn");
        float inkRight = 0;
        foreach ((Vector2 tl, Vector2 br) in sink.Quads)
        {
            inkRight = MathF.Max(inkRight, br.X);
        }

        // The ink can stop short of the advance (the last glyph's right-side bearing) but can never pass it by more
        // than a glyph's overhang; one tenth of the em is a generous bound for both.
        double drawnWidthDip = inkRight / font.DpiScale;
        Assert.InRange(drawnWidthDip, measured.Width - (font.Size * 0.1), measured.Width + (font.Size * 0.1));
        fs.Dispose();
    }

    [Fact]
    public void Text_MeasurementContext_IsTheFactorys()
    {
        using var dev = new HeadlessDevice();
        using var factory = new UiGraphicsFactory(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        IFont font = factory.CreateFont("Roboto", 16);
        Aprillz.MewUI.Text.ITextBackendMeasurementContext ctx = ((Aprillz.MewUI.Text.ITextBackendFactory)factory).CreateTextMeasurementContext(96);
        Size a = ctx.Measure("abc", font);
        Size b = TextMeasure.Measure("abc", (UiFont)font);
        Assert.Equal(b.Width, a.Width, 6);
        Assert.Equal(font.Ascent + font.Descent, a.Height, 6);  // one line box
        Assert.True(a.Width > 0);
    }

    private sealed class QuadCollector : IFontStashRenderer2
    {
        public List<(Vector2 TopLeft, Vector2 BottomRight)> Quads { get; } = [];

        public ITexture2DManager TextureManager { get; } = new NullTextures();

        public void DrawQuad(object texture, ref VertexPositionColorTexture topLeft, ref VertexPositionColorTexture topRight, ref VertexPositionColorTexture bottomLeft, ref VertexPositionColorTexture bottomRight)
        {
            Quads.Add((new Vector2(topLeft.Position.X, topLeft.Position.Y), new Vector2(bottomRight.Position.X, bottomRight.Position.Y)));
        }

        private sealed class NullTextures : ITexture2DManager
        {
            public object CreateTexture(int width, int height)
            {
                return new System.Drawing.Point(width, height);
            }

            public System.Drawing.Point GetTextureSize(object texture)
            {
                return (System.Drawing.Point)texture;
            }

            public void SetTextureData(object texture, System.Drawing.Rectangle bounds, byte[] data) { }
        }
    }
}
