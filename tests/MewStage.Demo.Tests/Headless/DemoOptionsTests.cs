using MewStage.Host;

using Xunit;

namespace MewStage.Demo.Tests.Headless;

/// <summary>The command line: mode flags, backend flags, the relaunch arguments and the unknown-argument report.</summary>
public sealed class DemoOptionsTests
{
    [Fact]
    public void NoFlags_IsTheStartPage()
    {
        var o = DemoOptions.Parse([]);
        Assert.Equal(DemoMode.StartPage, o.Mode);
        Assert.False(o.Warp);
        Assert.Empty(o.Unknown);
    }

    [Theory]
    [InlineData("--overlay", nameof(DemoMode.Overlay))]
    [InlineData("--workbench", nameof(DemoMode.Workbench))]
    [InlineData("--standard", nameof(DemoMode.Kit))]
    public void ModeFlags_SelectTheMode(string flag, string expected)
    {
        Assert.Equal(System.Enum.Parse<DemoMode>(expected), DemoOptions.Parse([flag]).Mode);
    }

    [Fact]
    public void LastModeFlagWins_Dx12FlagWinsOverVulkan()
    {
        var o = DemoOptions.Parse(["--overlay", "--render-vk", "--workbench", "--render-dx12", "--warp"]);
        Assert.Equal(DemoMode.Workbench, o.Mode);
        Assert.Equal(GpuBackend.Dx12, o.Backend);   // GpuBackends.Select: an explicit DX12 flag beats an explicit Vulkan flag
        Assert.True(o.Warp);
        Assert.Equal(["--render-dx12", "--warp"], o.BackendArgs());

        var vk = DemoOptions.Parse(["--render-vk"]);
        Assert.Equal(GpuBackend.Vulkan, vk.Backend);
        Assert.Equal(["--render-vk"], vk.BackendArgs());
    }

    [Fact]
    public void UnknownArguments_AreReported_NotSwallowed()
    {
        var o = DemoOptions.Parse(["--overlay", "--bogus", "extra"]);
        Assert.Equal(DemoMode.Overlay, o.Mode);
        Assert.Equal(["--bogus", "extra"], o.Unknown);
    }
}
