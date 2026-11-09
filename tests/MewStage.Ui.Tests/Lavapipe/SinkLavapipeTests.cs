using System;
using System.Collections.Generic;
using System.Globalization;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Aprillz.MewVG;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Vulkan;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Lavapipe;

/// <summary>
/// The NanoVG sink's rendering contract on a real (software) GPU. Each gate asserts the renderer's per-flush counter
/// for the path it means to exercise, so a pixel pass cannot come from a different path:
/// a concave fill is a triangle LIST (the vendored core tessellates; re-fanning it would mangle every concave shape),
/// a path clip keeps its full extent through its fringe rows, a 2400-point self-intersecting path arrives as a mask
/// fill drawn inside its shape only, gradient and image brushes show their paint (not white), a stencil clip confines
/// a fill and resets on Restore, a translucent self-intersecting star is one even coat, and a one-sided 1 px border
/// paints exactly its row.
/// </summary>
[Collection(UiLavapipeGate.Collection)]
public sealed class SinkLavapipeTests
{
    private const uint W = 256;
    private const uint H = 256;

    /// <summary>The clear color: 0.03, 0.03, 0.05 of 255.</summary>
    private static readonly ClearColor Clear = new(0.03f, 0.03f, 0.05f, 1f);

    private sealed record Frame(UiFrame.Image Img, int StencilOnceFills, int ConvexFills, int MaskFills, int ClipFringeDraws);

    private static string S(FormattableString s)
    {
        return s.ToString(CultureInfo.InvariantCulture);
    }

    private static Frame Render(VulkanDevice dev, Action<UiMewVGContext> draw, uint w = W, uint h = H)
    {
        (byte[] vs, byte[] ps) = TestAssets.UiShaders();
        using var vg = UiMewVGContext.Create(dev, vs, ps, Format.B8G8R8A8_UNorm, Format.D24_UNorm_S8_UInt);
        vg.BeginFrame(w, h, 1.0f);
        draw(vg);
        UiFrame.Image img = UiFrame.Vg(dev, vg, w, h, Clear);
        NanoVgRenderer r = vg.Renderer;
        return new Frame(img, r.LastFlushStencilOnceFills, r.LastFlushConvexFills, r.LastFlushMaskFills, r.LastFlushClipFringeDraws);
    }

    /// <summary>A real MewUI window through the production host at <paramref name="w"/> x <paramref name="h"/>.</summary>
    private static UiFrame.Image RenderWindow(VulkanDevice dev, Window window, uint w, uint h)
    {
        (byte[] vs, byte[] ps) = TestAssets.UiShaders();
        using var host = new EmbeddedUiHost(dev, vs, ps);
        host.Resize((int)w, (int)h, 1.0);
        host.SetWindows([window]);
        return UiFrame.Host(dev, host, w, h);
    }

    [SkippableFact]
    public void Sink_ConcaveFill_IsTriangleList()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();

        // An "L": (32,32)-(96,32)-(96,160)-(224,160)-(224,224)-(32,224), reflex vertex at (96,160). Its two arms fill
        // and its notch stays empty; a fan read of the core's triangle list paints the notch and holes the arms.
        Frame f = Render(dev, vg =>
        {
            vg.BeginPath();
            vg.MoveTo(32, 32);
            vg.LineTo(96, 32);
            vg.LineTo(96, 160);
            vg.LineTo(224, 160);
            vg.LineTo(224, 224);
            vg.LineTo(32, 224);
            vg.ClosePath();
            vg.FillColor(MewVGColor.RGBA(230, 60, 140, 255));
            vg.Fill();
        });

        static bool Ink((int R, int G, int B) p)
        {
            return p.R > 120 && p.R > p.G + 40;
        }

        int armHoles = 0;
        int notchInk = 0;
        for (int y = 40; y < 216; y += 4)
        {
            for (int x = 40; x < 216; x += 4)
            {
                bool inside = (x >= 34 && x <= 94 && y >= 34 && y <= 222) || (x >= 34 && x <= 222 && y >= 162 && y <= 222);
                bool notch = x >= 104 && x <= 214 && y >= 40 && y <= 150;
                if (inside && !Ink(f.Img.Px(x, y)))
                {
                    armHoles++;
                }

                if (notch && Ink(f.Img.Px(x, y)))
                {
                    notchInk++;
                }
            }
        }

        Console.WriteLine(S($"[concave-fill] armHoles={armHoles} notchInk={notchInk} stencilOnce={f.StencilOnceFills}"));
        Assert.True(Ink(f.Img.Px(64, 64)), S($"GATE: the L's vertical arm is not painted: {f.Img.Px(64, 64)}"));
        Assert.True(armHoles == 0, S($"{armHoles} unpainted samples INSIDE the concave polygon"));
        Assert.True(notchInk == 0, S($"{notchInk} painted samples INSIDE the notch (outside the polygon)"));
    }

    [SkippableFact]
    public void Sink_ClipEdge_WithinHalfPixel()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();

        // A triangle clip (a path, so it goes through RenderClip rather than the scissor), then a full-surface fill.
        // Its left edge is the vertical x = 40: column 40 (center 40.5) is inside, column 39 (center 39.5) is out.
        Frame f = Render(dev, vg =>
        {
            vg.BeginPath();
            vg.MoveTo(40, 30);
            vg.LineTo(220, 128);
            vg.LineTo(40, 226);
            vg.ClosePath();
            vg.Clip();
            vg.BeginPath();
            vg.Rect(0, 0, W, H);
            vg.FillColor(MewVGColor.RGBA(255, 255, 255, 255));
            vg.Fill();
        });

        (int R, int G, int B) inside = f.Img.Px(40, 128);
        (int R, int G, int B) outside = f.Img.Px(39, 128);
        (int R, int G, int B) deep = f.Img.Px(80, 128);
        Console.WriteLine(S($"[clip-edge] x40={inside} x39={outside} deep={deep} clipFringe={f.ClipFringeDraws}"));
        Assert.True(f.ClipFringeDraws >= 1, "the clip's fringe rows were not drawn");
        Assert.True(deep.R > 240, S($"GATE: the clipped fill is missing: {deep}"));
        Assert.True(inside.R > 240, S($"the clip lost its edge column (shrunk by half a pixel): x=40 is {inside}"));
        Assert.True(outside.R < 30, S($"the clip grew past its edge: x=39 is {outside}"));
    }

    [SkippableFact]
    public void Sink_MaskFill_UsedFor2000Points()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();

        // The core takes the mask route for a fill whose boundaries interact (it needs winding resolution) and that
        // has at least 2000 flattened points with a small device area per point. A pentagram is self-intersecting;
        // each of its 5 edges is split into 480 segments (2400 points over a ~220 px box). The split points zig-zag
        // a quarter pixel off the edge, because the core drops exactly collinear points before it counts them.
        const int PerEdge = 480;
        const float ZigZagPx = 0.25f;
        const float Cx = 128;
        const float Cy = 132;
        const float Radius = 110;
        Frame f = Render(dev, vg =>
        {
            vg.BeginPath();
            float px = 0;
            float py = 0;
            for (int i = 0; i <= 5; i++)
            {
                double a = (-Math.PI / 2) + (i * 2 * (2 * Math.PI / 5));
                float x = Cx + (float)(Radius * Math.Cos(a));
                float y = Cy + (float)(Radius * Math.Sin(a));
                if (i == 0)
                {
                    vg.MoveTo(x, y);
                }
                else
                {
                    float len = MathF.Sqrt(((x - px) * (x - px)) + ((y - py) * (y - py)));
                    float nx = -(y - py) / len;
                    float ny = (x - px) / len;
                    for (int k = 1; k <= PerEdge; k++)
                    {
                        float t = (float)k / PerEdge;
                        float off = k == PerEdge ? 0f : ((k & 1) == 0 ? ZigZagPx : -ZigZagPx);
                        vg.LineTo(px + ((x - px) * t) + (nx * off), py + ((y - py) * t) + (ny * off));
                    }
                }

                px = x;
                py = y;
            }

            vg.ClosePath();
            vg.FillRule(MewVGFillRule.NonZero);
            vg.FillColor(MewVGColor.RGBA(40, 200, 60, 255));
            vg.Fill();
        });

        (int R, int G, int B) center = f.Img.Px((int)Cx, (int)Cy);                      // winding 2
        (int R, int G, int B) point = f.Img.Px((int)Cx, (int)(Cy - (Radius * 0.8)));    // winding 1
        (int R, int G, int B) notch = f.Img.Px((int)Cx, (int)(Cy + (Radius * 0.6)));    // between the lower points: outside
        (int R, int G, int B) corner = f.Img.Px(6, 6);
        Console.WriteLine(S($"[mask] center={center} point={point} notch={notch} corner={corner} maskFills={f.MaskFills}"));
        Assert.True(f.MaskFills >= 1, "the 2400-point self-intersecting path did not arrive as a mask fill");
        Assert.True(center.G > 180 && point.G > 180, S($"the mask fill is missing inside: center {center}, point {point}"));
        Assert.True(notch.G < 30 && corner.G < 30, S($"the mask fill leaked outside its shape: notch {notch}, corner {corner}"));
    }

    [SkippableFact]
    public void Sink_TranslucentStar_IsOneEvenCoat()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();

        // A pentagram: its center pentagon has winding number 2, the five points winding 1. Non-zero fills both,
        // once; drawn plainly the overlapping body triangles and fringe rows would blend twice and show darker.
        const float Cx = 128;
        const float Cy = 132;
        const float Radius = 110;
        Frame f = Render(dev, vg =>
        {
            vg.BeginPath();
            for (int i = 0; i < 5; i++)
            {
                double a = (-Math.PI / 2) + (i * 2 * (2 * Math.PI / 5));
                float x = Cx + (float)(Radius * Math.Cos(a));
                float y = Cy + (float)(Radius * Math.Sin(a));
                if (i == 0)
                {
                    vg.MoveTo(x, y);
                }
                else
                {
                    vg.LineTo(x, y);
                }
            }

            vg.ClosePath();
            vg.FillRule(MewVGFillRule.NonZero);
            vg.FillColor(MewVGColor.RGBA(230, 40, 40, 128));   // half-transparent red
            vg.Fill();
        });

        (int R, int G, int B) center = f.Img.Px((int)Cx, (int)Cy);
        (int R, int G, int B) point = f.Img.Px((int)Cx, (int)(Cy - (Radius * 0.8)));
        Console.WriteLine(S($"[star] center={center} point={point} stencilOnce={f.StencilOnceFills} convex={f.ConvexFills}"));
        Assert.True(f.StencilOnceFills >= 1, "the star did not take the stencil-once path");
        Assert.True(point.R > 100, S($"GATE: the star's point is not painted: {point}"));
        Assert.True(Math.Abs(center.R - point.R) <= 2 && Math.Abs(center.G - point.G) <= 2 && Math.Abs(center.B - point.B) <= 2, S($"the overlap blended twice: center {center} vs point {point}"));
    }

    [SkippableFact]
    public void Sink_StencilClip_ConfinesFill_AndResetsOnRestore()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint Wc = 256;
        const uint Hc = 128;

        // Blue everywhere; clip to a centered rect; red everywhere (must show only inside the clip); Restore; a green
        // corner square (must show: the clip bit was cleared).
        Frame f = Render(
            dev,
            vg =>
            {
                vg.BeginPath();
                vg.Rect(0, 0, Wc, Hc);
                vg.FillColor(MewVGColor.RGBA(40, 90, 180, 255));
                vg.Fill();
                vg.Save();
                vg.BeginPath();
                vg.Rect(96, 32, 64, 64);
                vg.Clip();
                vg.BeginPath();
                vg.Rect(0, 0, Wc, Hc);
                vg.FillColor(MewVGColor.RGBA(220, 40, 40, 255));
                vg.Fill();
                vg.Restore();
                vg.BeginPath();
                vg.Rect(4, 4, 16, 16);
                vg.FillColor(MewVGColor.RGBA(40, 200, 80, 255));
                vg.Fill();
            },
            Wc,
            Hc);

        (int R, int G, int B) center = f.Img.Px(128, 64);
        (int R, int G, int B) above = f.Img.Px(128, 14);
        (int R, int G, int B) left = f.Img.Px(50, 64);
        (int R, int G, int B) green = f.Img.Px(12, 12);
        Console.WriteLine(S($"[stencil-clip] center={center} above={above} left={left} corner={green}"));
        Assert.True(center.R > 150 && center.R > center.B + 60 && center.R > center.G + 60, S($"the clip center is not red: {center}"));
        Assert.True(above.B > 110 && above.B > above.R + 40, S($"red leaked above the clip: {above}"));
        Assert.True(left.B > 110 && left.B > left.R + 40, S($"red leaked left of the clip: {left}"));
        Assert.True(green.G > 120 && green.G > green.R + 40 && green.G > green.B + 30, S($"the post-Restore green is missing (the clip was not reset): {green}"));
    }

    /// <summary>Draws the three brush probes in window coordinates: top half a gradient; bottom left an image brush; bottom right DrawImage.</summary>
    private sealed class BrushProbe : FrameworkElement
    {
        protected override void OnRender(IGraphicsContext context)
        {
            IGraphicsFactory f = GetGraphicsFactory();
            Rect b = Bounds;
            var stops = new List<GradientStop>
            {
                new(0.0, Color.FromArgb(255, 230, 20, 20)),
                new(1.0, Color.FromArgb(255, 20, 20, 230)),
            };
            var grad = new LinearGradientBrush(new Point(b.X, b.Y), new Point(b.X + b.Width, b.Y), stops);
            context.FillRectangle(new Rect(b.X, b.Y, b.Width, b.Height / 2), grad);

            IImage img = f.CreateImageFromBytes(SolidGreenBmp());
            var dest = new Rect(b.X, b.Y + (b.Height / 2), b.Width / 2, b.Height / 2);
            var brush = new ImageBrush(img, new Rect(0, 0, img.PixelWidth, img.PixelHeight), dest, TileMode.None);
            context.FillRectangle(dest, brush);
            context.DrawImage(img, new Rect(b.X + (b.Width / 2), b.Y + (b.Height / 2), b.Width / 2, b.Height / 2));
        }
    }

    /// <summary>A 2 x 2 24-bit BMP, every pixel (R, G, B) = (0, 200, 0); rows padded to 4 bytes.</summary>
    private static byte[] SolidGreenBmp()
    {
        const int Wb = 2;
        const int Hb = 2;
        const int Row = 8;
        const int PixelBytes = Row * Hb;
        const int Size = 54 + PixelBytes;
        byte[] d = new byte[Size];
        d[0] = (byte)'B';
        d[1] = (byte)'M';
        BitConverter.GetBytes(Size).CopyTo(d, 2);
        BitConverter.GetBytes(54).CopyTo(d, 10);
        BitConverter.GetBytes(40).CopyTo(d, 14);
        BitConverter.GetBytes(Wb).CopyTo(d, 18);
        BitConverter.GetBytes(Hb).CopyTo(d, 22);
        BitConverter.GetBytes((short)1).CopyTo(d, 26);
        BitConverter.GetBytes((short)24).CopyTo(d, 28);
        BitConverter.GetBytes(PixelBytes).CopyTo(d, 34);
        for (int y = 0; y < Hb; y++)
        {
            for (int x = 0; x < Wb; x++)
            {
                int o = 54 + (y * Row) + (x * 3);
                d[o] = 0;
                d[o + 1] = 200;
                d[o + 2] = 0;   // BGR
            }
        }

        return d;
    }

    [SkippableFact]
    public void Sink_GradientBrush_IsNotWhite()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();
        const uint Wp = 256;
        const uint Hp = 128;

        // MewUI turns shape anti-aliasing off around gradient and image-brush fills, so they arrive as one convex
        // row and take the single-pass convex branch; that branch must bind the paint type the uniform carries.
        Window window = new Window { Padding = Thickness.Zero }.Content(new BrushProbe());
        UiFrame.Image img = RenderWindow(dev, window, Wp, Hp);

        (int R, int G, int B) gl = img.Px(16, 32);             // gradient, near the red end
        (int R, int G, int B) gr = img.Px((int)Wp - 16, 32);   // gradient, near the blue end
        (int R, int G, int B) ib = img.Px(64, 96);             // image-brush rect
        (int R, int G, int B) di = img.Px(192, 96);            // DrawImage
        string report = S($"gradL={gl} gradR={gr} imageBrush={ib} drawImage={di}");
        Console.WriteLine("[brushes] " + report);

        static bool White((int R, int G, int B) p)
        {
            return p.R > 200 && p.G > 200 && p.B > 200;
        }

        Assert.False(White(gl) || White(gr) || White(ib) || White(di), "a brush or image fill rendered WHITE: " + report);
        Assert.True(gl.R > gl.B + 80, "the gradient start must be red: " + report);
        Assert.True(gr.B > gr.R + 80, "the gradient end must be blue: " + report);
        Assert.True(ib.G > ib.R + 80 && ib.G > ib.B + 80, "the image-brush rect must be green: " + report);
        Assert.True(di.G > di.R + 80 && di.G > di.B + 80, "DrawImage must be green: " + report);
    }

    [SkippableFact]
    public void Sink_OneSidedOnePixelBorder_PaintsExactlyItsRow()
    {
        UiLavapipeGate.Require();
        using var dev = new VulkanDevice();

        // A 1 px bottom edge only, white, on a 200 x 100 box at (28, 40) over an opaque black window.
        var border = new Border
        {
            Width = 200,
            Height = 100,
            NonUniformBorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Color.FromArgb(255, 255, 255, 255),
            Background = Color.FromArgb(0, 0, 0, 0),
        };
        var canvas = new Canvas();
        canvas.Add(border.CanvasPosition(28, 40));
        Window window = new Window { Padding = Thickness.Zero, Background = Color.FromArgb(255, 0, 0, 0) }.Content(canvas);
        UiFrame.Image img = RenderWindow(dev, window, W, H);

        int bottomRow = 40 + 100 - 1;
        (int R, int G, int B) edge = img.Px(128, bottomRow);
        (int R, int G, int B) above = img.Px(128, bottomRow - 1);
        (int R, int G, int B) below = img.Px(128, bottomRow + 1);
        (int R, int G, int B) top = img.Px(128, 40);
        (int R, int G, int B) left = img.Px(28, 90);
        (int R, int G, int B) right = img.Px(28 + 199, 90);
        Console.WriteLine(S($"[border] edge={edge} above={above} below={below} top={top} left={left} right={right}"));
        Assert.True(edge.R > 240, S($"the 1 px bottom edge is not a full-strength row: {edge}"));
        Assert.True(above.R < 30 && below.R < 30, S($"the edge bled into the neighboring rows: above {above}, below {below}"));
        Assert.True(top.R < 30 && left.R < 30 && right.R < 30, S($"a side with zero thickness was painted: top {top}, left {left}, right {right}"));
    }
}
