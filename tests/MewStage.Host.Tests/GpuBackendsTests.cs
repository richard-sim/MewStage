using System;
using System.IO;

using Xunit;

namespace MewStage.Host.Tests;

/// <summary>Backend selection by flag or OS, and the cooked-shader loader's paths and messages.</summary>
public sealed class GpuBackendsTests
{
    [Fact]
    public void Select_FlagsOverride_ElseTheOsDefault()
    {
        Assert.Equal(GpuBackend.Dx12, GpuBackends.Select(["--overlay", "--render-dx12"]));
        Assert.Equal(GpuBackend.Vulkan, GpuBackends.Select(["--render-vk"]));
        Assert.Equal(GpuBackend.Dx12, GpuBackends.Select(["--render-dx12", "--render-vk"]));   // the first flag wins
        GpuBackend byOs = GpuBackends.Select([]);
        Assert.Equal(OperatingSystem.IsWindows() ? GpuBackend.Dx12 : GpuBackend.Vulkan, byOs);
    }

    [Fact]
    public void CookedShaders_LoadTheCommittedBlobs()
    {
        (byte[] vs, byte[] ps) = CookedShaders.Load(GpuBackend.Vulkan, "ui");
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(vs, 0));   // SPIR-V magic
        Assert.Equal(0x07230203u, BitConverter.ToUInt32(ps, 0));
        (byte[] dvs, _) = CookedShaders.Load(GpuBackend.Dx12, "cube");
        Assert.Equal("DXBC", System.Text.Encoding.ASCII.GetString(dvs, 0, 4));   // the DXIL container
        Assert.NotEmpty(CookedShaders.LoadStage(GpuBackend.Vulkan, "grid", "PSMain"));
    }

    [Fact]
    public void CookedShaders_MissingBlob_NamesTheCookCommand()
    {
        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(() => CookedShaders.Load(GpuBackend.Vulkan, "no-such-shader"));
        Assert.Contains("cook_shaders.py", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no-such-shader.hlsl.VSMain.spv", ex.Message, StringComparison.Ordinal);
    }
}
