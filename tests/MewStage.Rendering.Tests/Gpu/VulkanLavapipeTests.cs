using System;
using System.Security.Cryptography;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Vulkan;

using Xunit;

namespace MewStage.Rendering.Tests.Gpu;

/// <summary>
/// The Vulkan backend on a real implementation (Mesa lavapipe, software): device creation, memory, dynamic rendering,
/// synchronization2, submit and readback; a draw through a graphics pipeline from cooked SPIR-V, directly on a
/// command list and through the render graph + scheduler with a transient depth attachment; and a structural golden
/// fingerprint that catches rasterization, interpolation, viewport, winding and clear regressions without a
/// pixel-exact hash (Mesa versions differ in the last bit).
/// Gated by MEWSTAGE_VK_SMOKE=1 and a working loader; every test here counts itself through <see cref="LavapipeGate"/>.
/// </summary>
[Collection(LavapipeGate.Collection)]
public sealed class VulkanLavapipeTests
{
    [SkippableFact]
    public void Offscreen_Clear_ReadsBack_ExactColor()
    {
        LavapipeGate.Require();
        OffscreenImage img = VulkanOffscreen.ClearAndReadback(16, 16, 0.25f, 0.50f, 0.75f);
        (byte r, byte g, byte b, byte a) = img.Pixel(8, 8);

        // R8G8B8A8_UNORM round-trip of the clear color (+-1 LSB for rounding).
        Assert.InRange(r, 63, 65);
        Assert.InRange(g, 127, 129);
        Assert.InRange(b, 190, 192);
        Assert.Equal(255, a);
    }

    [SkippableFact]
    public void Draws_VertexColoredTriangle_OnABareCommandList()
    {
        LavapipeGate.Require();
        const uint W = 32, H = 32;
        using var dev = new VulkanDevice();
        (byte[] vs, byte[] ps) = TestShaders.Triangle(spirv: true);

        using IPipelineLayout layout = dev.CreatePipelineLayout(new PipelineLayoutDesc { DebugName = "empty" });
        using IRenderPipeline pso = dev.CreateRenderPipeline(new RenderPipelineDesc
        {
            Name = "triangle",
            VertexShader = vs,
            PixelShader = ps,
            VertexEntry = "VSMain",
            PixelEntry = "PSMain",
            Layout = layout,
            VertexAttributes = [],
            ColorFormats = [Format.R8G8B8A8_UNorm],
            DepthFormat = Format.Unknown,
            Cull = CullMode.None,
            Topology = PrimitiveTopology.TriangleList,
            Blend = BlendMode.Opaque,
            SampleCount = 1,
        });

        using ITexture tex = dev.CreateTexture(new TextureDesc { Dimension = TextureDimension.Tex2D, Format = Format.R8G8B8A8_UNorm, Width = W, Height = H, DepthOrArrayLayers = 1, MipLevels = 1, SampleCount = 1, Usage = ResourceUsage.RenderTarget | ResourceUsage.CopySource });
        using IBuffer readback = dev.CreateBuffer(new BufferDesc { SizeBytes = W * H * 4, Memory = MemoryClass.Readback, Usage = ResourceUsage.CopyDest });

        ICommandList cmd = dev.GraphicsQueue.AcquireCommandList();
        var rec = (IGraphicsRecorder)cmd;
        cmd.Begin();
        Span<ResourceBarrierDesc> toRt = [ResourceBarrierDesc.Transition(tex.Handle, ResourceState.Common, ResourceState.RenderTarget)];
        cmd.ResourceBarrier(toRt);
        Span<ColorAttachment> ca = [new ColorAttachment { TextureHandle = tex.Handle, Clear = true, ClearValue = ClearColor.Black }];
        rec.BeginRendering(ca, default, W, H);
        rec.SetPipeline(pso);
        rec.SetViewportScissor(0, 0, W, H);
        rec.Draw(3, 1, 0, 0);
        rec.EndRendering();
        Span<ResourceBarrierDesc> toCopy = [ResourceBarrierDesc.Transition(tex.Handle, ResourceState.RenderTarget, ResourceState.CopySource)];
        cmd.ResourceBarrier(toCopy);
        cmd.CopyTextureToBuffer(tex, readback, W, H);
        cmd.Close();

        using IFence fence = dev.CreateFence(0);
        dev.GraphicsQueue.Submit([cmd], default, [new FenceSignal(fence, 1)]);
        fence.Wait(1);

        byte[] rgba = new byte[W * H * 4];
        System.Runtime.InteropServices.Marshal.Copy(((IMappedBuffer)readback).MappedPointer, rgba, 0, rgba.Length);
        int c0 = ((16 * (int)W) + 16) * 4;
        int sum = rgba[c0] + rgba[c0 + 1] + rgba[c0 + 2];
        Assert.True(sum > 10, $"center pixel not colored by the triangle: {rgba[c0]},{rgba[c0 + 1]},{rgba[c0 + 2]}");
    }

    [SkippableFact]
    public void Draws_Triangle_ThroughGraphAndScheduler_WithTransientDepth()
    {
        LavapipeGate.Require();
        using var dev = new VulkanDevice();
        byte[] px = TriangleScene.Render(dev, spirv: true, out string[] passes);

        // Gate first: both passes survived culling (otherwise the pixel assertions prove nothing).
        Assert.Contains("triangle", passes);
        Assert.Contains("readback", passes);

        const int S = (int)TriangleScene.Size;
        (byte r, byte g, byte b) = TriangleScene.Pixel(px, S / 2, S / 2);
        Assert.True(r + g + b > 20, "center not colored: the depth test (GreaterEqual against a 0 clear) or the draw failed");
        (byte r, byte g, byte b) corner = TriangleScene.Pixel(px, 1, 1);
        Assert.True(corner.r + corner.g + corner.b < 12, "corner not clear");
    }

    [SkippableFact]
    public void GoldenTriangle_HasClearCornersAndInterpolatedVertexColors()
    {
        LavapipeGate.Require();
        using var dev = new VulkanDevice();
        byte[] px = TriangleScene.Render(dev, spirv: true, out _);
        const int W = (int)TriangleScene.Size, H = (int)TriangleScene.Size;

        // Corners are outside the triangle: clear (black).
        foreach ((int x, int y) in new[] { (1, 1), (W - 2, 1), (1, H - 2), (W - 2, H - 2) })
        {
            (byte r, byte g, byte b) = TriangleScene.Pixel(px, x, y);
            Assert.True(r + g + b < 12, $"corner ({x},{y}) not clear: {r},{g},{b}");
        }

        // Center is inside: colored.
        (byte r, byte g, byte b) ctr = TriangleScene.Pixel(px, W / 2, H / 2);
        Assert.True(ctr.r + ctr.g + ctr.b > 20, "center not colored");

        // Per-vertex dominant channels (Vulkan NDC Y down: v0 top = red, v1 bottom-right = green, v2 bottom-left = blue).
        (byte r, byte g, byte b) top = TriangleScene.Pixel(px, W / 2, 16);
        Assert.True(top.r >= top.g && top.r >= top.b, $"top not red-dominant: {top.r},{top.g},{top.b}");
        (byte r, byte g, byte b) br = TriangleScene.Pixel(px, W - 18, H - 16);
        Assert.True(br.g >= br.r && br.g >= br.b, $"bottom-right not green-dominant: {br.r},{br.g},{br.b}");
        (byte r, byte g, byte b) bl = TriangleScene.Pixel(px, 17, H - 16);
        Assert.True(bl.b >= bl.r && bl.b >= bl.g, $"bottom-left not blue-dominant: {bl.r},{bl.g},{bl.b}");
    }

    [SkippableFact]
    public void GoldenTriangle_IsDeterministic_AcrossRuns()
    {
        LavapipeGate.Require();
        byte[] a;
        byte[] b;
        using (var dev = new VulkanDevice())
        {
            a = TriangleScene.Render(dev, spirv: true, out _);
        }

        using (var dev = new VulkanDevice())
        {
            b = TriangleScene.Render(dev, spirv: true, out _);
        }

        Assert.Equal(Convert.ToHexString(SHA256.HashData(a)), Convert.ToHexString(SHA256.HashData(b)));
    }
}
