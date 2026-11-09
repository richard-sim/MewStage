using System;
using System.Runtime.Versioning;

using MewStage.Rendering.Backends.Dx12;

using Xunit;

namespace MewStage.Rendering.Tests.Gpu;

/// <summary>
/// The DX12 backend end to end on Windows: the triangle fixture (cooked DXIL) through the graph + scheduler with a
/// transient depth attachment, read back and checked. Runs on the WARP software adapter so any Windows machine can
/// run it; it compiles everywhere and skips off Windows. Lavapipe cannot show DX12-specific defects (descriptor
/// heaps, root signatures, resource-state validation), so this is the one GPU test the Linux CI cannot stand in for.
/// </summary>
public sealed class Dx12SmokeTests
{
    [SkippableFact]
    [SupportedOSPlatform("windows")]
    public void Dx12_Warp_DrawsTriangle_ThroughGraphAndScheduler()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "DX12 is Windows-only");
        using var dev = new Dx12Device(useWarp: true);
        byte[] px = TriangleScene.Render(dev, spirv: false, out string[] passes);

        Assert.Contains("triangle", passes);
        Assert.Contains("readback", passes);

        const int S = (int)TriangleScene.Size;
        (byte r, byte g, byte b) = TriangleScene.Pixel(px, S / 2, S / 2);
        Assert.True(r + g + b > 20, $"center not colored: {r},{g},{b}");
        (byte r, byte g, byte b) corner = TriangleScene.Pixel(px, 1, 1);
        Assert.True(corner.r + corner.g + corner.b < 12, "corner not clear");

        // D3D clip Y is up, so the red vertex (clip y = -0.6) lands at the BOTTOM on DX12, the Vulkan picture flipped.
        (byte r, byte g, byte b) bottom = TriangleScene.Pixel(px, S / 2, S - 16);
        Assert.True(bottom.r >= bottom.g && bottom.r >= bottom.b, $"bottom not red-dominant: {bottom.r},{bottom.g},{bottom.b}");
    }
}
