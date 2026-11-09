using System;

using Aprillz.MewVG;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Headless;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

namespace MewStage.Ui.Tests.Headless;

/// <summary>
/// The NanoVG renderer's CPU path on the headless device: a synthetic frame (a filled panel + a stroked line) uploads
/// vertices and per-draw uniforms through the renderer's own buffers and issues draws; glyph triangles go through
/// the text path; a steady-state redraw allocates nothing in the sink; and the per-draw view CBV is sized by the
/// worst-case bind count so a clip-heavy frame can never overrun it.
/// </summary>
public sealed class NanoVgSinkTests
{
    private static UiMewVGContext NewVg(HeadlessDevice dev)
    {
        return UiMewVGContext.Create(dev, TestAssets.DummyShader, TestAssets.DummyShader, Format.B8G8R8A8_UNorm, Format.D24_UNorm_S8_UInt);
    }

    private static void BuildFrame(UiMewVGContext vg)
    {
        vg.BeginFrame(256, 128, 1.0f);

        // A filled panel (a convex rounded rect: body + anti-aliasing fringe).
        vg.BeginPath();
        vg.RoundedRect(16, 16, 200, 80, 8);
        vg.FillColor(MewVGColor.RGBA(40, 80, 160, 255));
        vg.Fill();

        // A stroked underline (a single convex stroke path).
        vg.BeginPath();
        vg.MoveTo(20, 110);
        vg.LineTo(230, 110);
        vg.StrokeWidth(3f);
        vg.StrokeColor(MewVGColor.RGBA(220, 220, 220, 255));
        vg.Stroke();
    }

    [Fact]
    public void SyntheticFrame_IssuesDrawsAndUploadsThroughBuffers()
    {
        using var dev = new HeadlessDevice();
        using UiMewVGContext vg = NewVg(dev);
        var rec = new RecordingRecorder();

        BuildFrame(vg);
        vg.Begin(rec);
        vg.EndFrame();   // Flush: records onto rec

        Assert.True(rec.DrawCalls > 0, "no draw calls issued");
        Assert.True(rec.TotalVertices > 0, "no vertices drawn");
        Assert.True(rec.VertexBufferBinds >= 1, "the vertex buffer was never bound (no vertex upload)");
        Assert.True(rec.ConstantBufferBinds > 0, "the per-draw uniform CBV was never bound");
        Assert.True(rec.PipelineSets > 0, "no pipeline bound");

        // CBV offsets are 256-byte-stride sub-allocations into the uniform ring (per-draw uniforms).
        Assert.All(rec.CbvOffsets, o => Assert.Equal(0ul, o % 256ul));
        Assert.Contains(rec.CbvOffsets, o => o > 0);   // a later uniform set bound at a non-zero ring offset
    }

    [Fact]
    public void TextSink_GlyphTrianglesAreRecorded()
    {
        using var dev = new HeadlessDevice();
        using UiMewVGContext vg = NewVg(dev);
        var rec = new RecordingRecorder();
        Span<NVGvertex> tris =
        [
            new NVGvertex(10, 10, 0, 0), new NVGvertex(30, 10, 1, 0), new NVGvertex(10, 30, 0, 1),
            new NVGvertex(30, 10, 1, 0), new NVGvertex(30, 30, 1, 1), new NVGvertex(10, 30, 0, 1),
        ];
        vg.BeginFrame(64, 64, 1f);
        vg.Renderer.DrawTextTriangles(tris, 0, MewVGColor.RGBA(255, 255, 255, 255));   // image 0: the dummy; type 3 text path
        vg.Begin(rec);
        vg.EndFrame();
        Assert.True(rec.DrawCalls >= 1, "glyph triangles not drawn");
        Assert.Equal(6u, rec.TotalVertices);
    }

    [Fact]
    public void Sink_SteadyState_AllocatesZero()
    {
        using var dev = new HeadlessDevice();
        using UiMewVGContext vg = NewVg(dev);
        var rec = new RecordingRecorder();

        // Warm up: grow the CPU arrays and GPU buffers, then settle.
        for (int i = 0; i < 20; i++)
        {
            BuildFrame(vg);
            vg.Begin(rec);
            vg.EndFrame();
            rec.Reset();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long best = long.MaxValue;
        for (int window = 0; window < 3; window++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 60; i++)
            {
                BuildFrame(vg);
                vg.Begin(rec);
                vg.EndFrame();
                rec.Reset();
            }

            best = Math.Min(best, (GC.GetAllocatedBytesForCurrentThread() - before) / 60);
        }

        // The renderer's own staging (calls, paths, vertices, uniforms, the GPU rings) is reused: zero. The vendored
        // core's tessellator is covered by the allocation vendor patches and is zero too.
        Assert.Equal(0, best);
    }

    [Fact]
    public void ViewCbv_IsSizedForTheWorstCaseBindCount_OnAClipHeavyFrame()
    {
        // The per-draw view CBV is written one slot per BIND, and one recorded call issues up to four binds (a clip:
        // winding, fringe, mark, clear). Sizing it by the uniform count under-counts clip-heavy frames and lets the
        // write run past the mapped buffer (on DX12 that corrupts the adjacent paint CBV).
        using var dev = new HeadlessDevice();
        using var r = new NanoVgRenderer(dev, TestAssets.DummyShader, TestAssets.DummyShader, Format.B8G8R8A8_UNorm, Format.D24_UNorm_S8_UInt);
        var rec = new RecordingRecorder();

        r.BeginFrame(256, 128, 1.0f);
        const int Clips = 40;
        const int Fills = 4;
        var scissor = new NVGscissorState();
        Span<NVGvertex> glyph =
        [
            new NVGvertex(10, 10, 0, 0), new NVGvertex(30, 10, 1, 0), new NVGvertex(10, 30, 0, 1),
            new NVGvertex(30, 10, 1, 0), new NVGvertex(30, 30, 1, 1), new NVGvertex(10, 30, 0, 1),
        ];
        for (int i = 0; i < Clips; i++)
        {
            r.RenderClip(ref scissor, 1.0f, [], [], []);
            if (i % 10 == 0)
            {
                r.DrawTextTriangles(glyph, 0, MewVGColor.RGBA(255, 255, 255, 255));
            }
        }

        r.Begin(rec);
        r.Flush();

        // A clip binds for its winding, mark and clear passes (the fringe pass only when it has fringe rows, and an
        // empty clip has none): 3 binds for 1 uniform set; each glyph fill is 1 bind.
        Assert.Equal((Clips * 3) + Fills, r.ViewDrawSeq);
        Assert.True(r.ViewCbvCapacitySlots >= r.ViewDrawSeq, $"view CBV undersized: capacity={r.ViewCbvCapacitySlots} slots < binds={r.ViewDrawSeq}");
        Assert.False(r.ViewGuardWarnedThisFlush, "the view-CBV overflow fail-safe fired: the buffer was undersized");
        Assert.True(rec.DrawCalls > 0, "no draws issued");
    }

    [Fact]
    public void DescriptorsPerDraw_IsTheShaderBindingCount()
    {
        // b0 + b1 CBVs, t0 + t1 SRVs: what ui.hlsl declares and what the DX12 per-frame descriptor budget is sized against.
        Assert.Equal(4, NanoVgRenderer.DescriptorsPerDraw);
    }
}
