using System;
using System.Runtime.InteropServices;

using MewStage.Graphics;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;

namespace MewStage.Rendering.Tests.Gpu;

/// <summary>
/// Renders the triangle fixture through the whole stack on any backend: GpuScheduler frame, a RenderGraph with an
/// imported color target, a TRANSIENT reversed-Z depth attachment (cleared to 0, GreaterEqual test, the triangle
/// sits at z = 0.5 so it passes), a second pass that copies the target into an imported readback buffer, the
/// synthesized barriers in between, and the frame fence. What comes back is the RGBA8 image.
/// </summary>
public static class TriangleScene
{
    /// <summary>Render size. 64 x 4 B = 256 B rows, so a DX12 readback needs no row de-padding.</summary>
    public const uint Size = 64;

    private static RgTextureDesc Tex(string name, Format f)
    {
        return new() { Name = name, Format = f, Width = Size, Height = Size, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 };
    }

    /// <summary>Renders one frame and reads the target back.</summary>
    /// <param name="dev">The device (Vulkan, DX12 or headless).</param>
    /// <param name="spirv">Which cooked blobs to use.</param>
    /// <param name="passNames">Receives the compiled pass names (a culling gate for the caller).</param>
    /// <returns>Size x Size RGBA8 pixels, top row first.</returns>
    public static byte[] Render(IGraphicsDevice dev, bool spirv, out string[] passNames)
    {
        ArgumentNullException.ThrowIfNull(dev);
        (byte[] vs, byte[] ps) = TestShaders.Triangle(spirv);
        using IPipelineLayout layout = dev.CreatePipelineLayout(new PipelineLayoutDesc { DebugName = "triangle-empty" });
        using IRenderPipeline pso = dev.CreateRenderPipeline(new RenderPipelineDesc
        {
            Name = "triangle",
            VertexShader = vs,
            PixelShader = ps,
            VertexEntry = "VSMain",
            PixelEntry = "PSMain",
            Layout = layout,
            VertexAttributes = [],
            VertexStride = 0,
            ColorFormats = [Format.R8G8B8A8_UNorm],
            DepthFormat = Format.D32_Float,
            DepthTest = true,
            DepthWrite = true,
            DepthCompare = DepthCompare.GreaterEqual,
            Cull = CullMode.None,
            Topology = PrimitiveTopology.TriangleList,
            Blend = BlendMode.Opaque,
            SampleCount = 1,
        });

        using ITexture target = dev.CreateTexture(new TextureDesc
        {
            Dimension = TextureDimension.Tex2D,
            Format = Format.R8G8B8A8_UNorm,
            Width = Size,
            Height = Size,
            DepthOrArrayLayers = 1,
            MipLevels = 1,
            SampleCount = 1,
            Usage = ResourceUsage.RenderTarget | ResourceUsage.CopySource,
            DebugName = "triangle-target",
        });
        using IBuffer readback = dev.CreateBuffer(new BufferDesc { SizeBytes = Size * Size * 4, Memory = MemoryClass.Readback, Usage = ResourceUsage.CopyDest, DebugName = "triangle-readback" });

        using var sched = new GpuScheduler(dev);
        FrameContext ctx = sched.BeginFrame();
        var graph = new RenderGraph();
        RgHandle color = graph.ImportTexture(Tex("target", Format.R8G8B8A8_UNorm), target.Handle, ResourceState.Common);
        RgHandle rb = graph.ImportBuffer(new RgBufferDesc { Name = "readback", SizeBytes = Size * Size * 4 }, readback.Handle, ResourceState.Common);
        RgHandle depth = default;
        graph.AddPass("triangle", PassKind.Graphics, s =>
        {
            depth = s.CreateDepth(Tex("depth", Format.D32_Float));
            s.WriteColor(color);
            s.SetExecute((in c) =>
            {
                IGraphicsRecorder rec = c.Recorder;
                Span<ColorAttachment> ca = [new ColorAttachment { TextureHandle = c.Resolve(color), Clear = true, ClearValue = ClearColor.Black }];
                var da = new DepthAttachment { TextureHandle = c.Resolve(depth), Clear = true, ClearDepth = 0f };
                rec.BeginRendering(ca, in da, Size, Size);
                rec.SetPipeline(pso);
                rec.SetViewportScissor(0, 0, Size, Size);
                rec.Draw(3, 1, 0, 0);
                rec.EndRendering();
            });
        });
        graph.AddPass("readback", PassKind.Graphics, s =>
        {
            s.Read(color, ShaderStage.Pixel, ResourceState.CopySource);
            s.Write(rb, ResourceState.CopyDest);
            s.SetExecute((in c) => c.CommandList.CopyTextureToBuffer(target, readback, Size, Size));
        });

        CompiledRenderGraph compiled = sched.Submit(graph, ctx);
        passNames = new string[compiled.Passes.Count];
        for (int i = 0; i < passNames.Length; i++)
        {
            passNames[i] = compiled.Passes[i].Name;
        }

        sched.EndFrame(ctx);
        ctx.FrameFence.Wait(ctx.FenceValue);

        byte[] rgba = new byte[Size * Size * 4];
        IntPtr p = ((IMappedBuffer)readback).MappedPointer;
        if (p != IntPtr.Zero)
        {
            Marshal.Copy(p, rgba, 0, rgba.Length);
        }

        return rgba;
    }

    /// <summary>One pixel of a <see cref="Render"/> result.</summary>
    /// <param name="rgba">The image.</param>
    /// <param name="x">Column.</param>
    /// <param name="y">Row (top is 0).</param>
    /// <returns>R, G, B.</returns>
    public static (byte R, byte G, byte B) Pixel(byte[] rgba, int x, int y)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        int i = ((y * (int)Size) + x) * 4;
        return (rgba[i], rgba[i + 1], rgba[i + 2]);
    }
}
