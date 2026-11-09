using MewStage.Host;

namespace MewStage.Demo;

/// <summary>The cooked cube and grid shaders for one backend, loaded once per mode entry.</summary>
/// <param name="CubeVs">cube.hlsl VSMain.</param>
/// <param name="CubePs">cube.hlsl PSMain.</param>
/// <param name="GridVs">grid.hlsl VSMain.</param>
/// <param name="GridPs">grid.hlsl PSMain.</param>
internal sealed record SceneShaders(byte[] CubeVs, byte[] CubePs, byte[] GridVs, byte[] GridPs)
{
    /// <summary>Load the four stages for <paramref name="backend"/> from the cooked assets.</summary>
    public static SceneShaders Load(GpuBackend backend)
    {
        return new SceneShaders(
            CookedShaders.LoadStage(backend, "cube", "VSMain"),
            CookedShaders.LoadStage(backend, "cube", "PSMain"),
            CookedShaders.LoadStage(backend, "grid", "VSMain"),
            CookedShaders.LoadStage(backend, "grid", "PSMain"));
    }
}
