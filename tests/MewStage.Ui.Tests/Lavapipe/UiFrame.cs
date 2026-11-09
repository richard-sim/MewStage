using System;
using System.Runtime.InteropServices;

using MewStage.Graphics;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;
using MewStage.Ui.Backend;

using Xunit;

namespace MewStage.Ui.Tests.Lavapipe;

/// <summary>
/// One UI frame on a real device, read back as BGRA8: the host's <c>ui</c> pass (or a bare MewVG frame) goes into a
/// render graph with an imported color target and an imported readback buffer, the scheduler compiles it (the
/// compiler owns every barrier, the transient pool backs the stencil) and executes it, and the test reads the pixels.
/// This is the path the application drives every frame; nothing here records a barrier by hand.
/// </summary>
public static class UiFrame
{
    /// <summary>A BGRA8 image with pixel accessors.</summary>
    public sealed class Image
    {
        /// <summary>Creates an image over <paramref name="bgra"/>.</summary>
        public Image(byte[] bgra, uint width, uint height)
        {
            Bgra = bgra;
            Width = width;
            Height = height;
        }

        /// <summary>The raw bytes, row-major, 4 per pixel (B, G, R, A).</summary>
        public byte[] Bgra { get; }

        /// <summary>Width in pixels.</summary>
        public uint Width { get; }

        /// <summary>Height in pixels.</summary>
        public uint Height { get; }

        /// <summary>The (R, G, B) of one pixel.</summary>
        public (int R, int G, int B) Px(int x, int y)
        {
            int o = ((y * (int)Width) + x) * 4;
            return (Bgra[o + 2], Bgra[o + 1], Bgra[o]);
        }

        /// <summary>The mean (R, G, B) over a block.</summary>
        public (double R, double G, double B) Mean(int x0, int y0, int w, int h)
        {
            double r = 0;
            double g = 0;
            double b = 0;
            int n = 0;
            for (int y = y0; y < y0 + h; y++)
            {
                for (int x = x0; x < x0 + w; x++)
                {
                    (int pr, int pg, int pb) = Px(x, y);
                    r += pr;
                    g += pg;
                    b += pb;
                    n++;
                }
            }

            return (r / n, g / n, b / n);
        }
    }

    /// <summary>Pass colors in the device order: BGRA8, premultiplied.</summary>
    private static RgTextureDesc ColorDesc(uint w, uint h)
    {
        return new RgTextureDesc { Name = "ui-color", Format = Format.B8G8R8A8_UNorm, Width = w, Height = h, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 };
    }

    /// <summary>
    /// One host frame at <paramref name="w"/> x <paramref name="h"/>. <paramref name="beforeUi"/> may add passes that
    /// write the target before the ui pass (a "world" that the UI then loads instead of clearing).
    /// </summary>
    public static Image Host(IGraphicsDevice dev, EmbeddedUiHost host, uint w, uint h, bool clearBackground = true, long? animationTimestamp = null, Action<RenderGraph, RgHandle>? beforeUi = null)
    {
        ArgumentNullException.ThrowIfNull(dev);
        ArgumentNullException.ThrowIfNull(host);
        return Render(dev, w, h, (graph, color) =>
        {
            beforeUi?.Invoke(graph, color);
            Assert.True(host.TickAndRender(graph, color, UiInvalidationMode.Continuous, clearBackground, animationTimestamp), "the host added no ui pass");
        });
    }

    /// <summary>
    /// One bare MewVG frame: the caller has recorded into <paramref name="vg"/> (already in BeginFrame), and a graph
    /// pass clears the target to <paramref name="clear"/>, clears the stencil and flushes the frame.
    /// </summary>
    public static Image Vg(IGraphicsDevice dev, UiMewVGContext vg, uint w, uint h, ClearColor clear)
    {
        ArgumentNullException.ThrowIfNull(dev);
        ArgumentNullException.ThrowIfNull(vg);
        return Render(dev, w, h, (graph, color) =>
        {
            RgHandle depth = default;
            graph.AddPass("vg", PassKind.Graphics, s =>
            {
                s.Write(color, ResourceState.RenderTarget);
                depth = s.CreateDepth(new RgTextureDesc { Name = "vg-stencil", Format = Format.D24_UNorm_S8_UInt, Width = w, Height = h, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 });
                s.SetExecute((in c) =>
                {
                    IGraphicsRecorder rec = c.Recorder;
                    Span<ColorAttachment> ca = [new ColorAttachment { TextureHandle = c.Resolve(color), Clear = true, ClearValue = clear }];
                    var da = new DepthAttachment { TextureHandle = c.Resolve(depth), Clear = true, ClearDepth = 0f, HasStencil = true, ClearStencilOnLoad = true, ClearStencil = 0 };
                    rec.BeginRendering(ca, in da, w, h);
                    rec.SetViewportScissor(0, 0, w, h);
                    vg.Begin(rec);
                    vg.EndFrame();
                    rec.EndRendering();
                });
            });
        });
    }

    private static Image Render(IGraphicsDevice dev, uint w, uint h, Action<RenderGraph, RgHandle> build)
    {
        using ITexture target = dev.CreateTexture(new TextureDesc
        {
            Dimension = TextureDimension.Tex2D,
            Format = Format.B8G8R8A8_UNorm,
            Width = w,
            Height = h,
            DepthOrArrayLayers = 1,
            MipLevels = 1,
            SampleCount = 1,
            Usage = ResourceUsage.RenderTarget | ResourceUsage.CopySource,
            DebugName = "ui-color",
        });
        using IBuffer readback = dev.CreateBuffer(new BufferDesc { SizeBytes = w * h * 4, Memory = MemoryClass.Readback, Usage = ResourceUsage.CopyDest, DebugName = "ui-readback" });

        using var sched = new GpuScheduler(dev);
        FrameContext ctx = sched.BeginFrame();
        var graph = new RenderGraph();
        RgHandle color = graph.ImportTexture(ColorDesc(w, h), target.Handle, ResourceState.Common);
        RgHandle rb = graph.ImportBuffer(new RgBufferDesc { Name = "ui-readback", SizeBytes = w * h * 4 }, readback.Handle, ResourceState.Common);
        build(graph, color);
        graph.AddPass("readback", PassKind.Graphics, s =>
        {
            s.Read(color, ShaderStage.Pixel, ResourceState.CopySource);
            s.Write(rb, ResourceState.CopyDest);
            s.SetExecute((in c) => c.CommandList.CopyTextureToBuffer(target, readback, w, h));
        });

        sched.Submit(graph, ctx);
        sched.EndFrame(ctx);
        ctx.FrameFence.Wait(ctx.FenceValue);

        byte[] bgra = new byte[w * h * 4];
        IntPtr p = ((IMappedBuffer)readback).MappedPointer;
        Assert.NotEqual(IntPtr.Zero, p);
        Marshal.Copy(p, bgra, 0, bgra.Length);
        return new Image(bgra, w, h);
    }
}
