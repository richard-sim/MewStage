// The GPU backend a window renders with, and the cooked shaders for it, so every window and tool selects and loads
// them the same way.
using System;
using System.IO;

namespace MewStage.Host;

/// <summary>The GPU API a <see cref="GpuWindow"/> renders with.</summary>
public enum GpuBackend
{
    /// <summary>Direct3D 12 (Windows).</summary>
    Dx12,

    /// <summary>Vulkan (every platform; the only backend that runs offscreen on lavapipe).</summary>
    Vulkan,
}

/// <summary>Backend selection: DX12 on Windows, Vulkan elsewhere, unless <c>--render-dx12</c> / <c>--render-vk</c>.</summary>
public static class GpuBackends
{
    /// <summary>The command-line flag that forces DX12.</summary>
    public const string Dx12Flag = "--render-dx12";

    /// <summary>The command-line flag that forces Vulkan.</summary>
    public const string VulkanFlag = "--render-vk";

    /// <summary>Pick the backend for this OS unless an explicit override flag is present.</summary>
    public static GpuBackend Select(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Array.IndexOf(args, Dx12Flag) >= 0)
        {
            return GpuBackend.Dx12;
        }

        if (Array.IndexOf(args, VulkanFlag) >= 0)
        {
            return GpuBackend.Vulkan;
        }

        return OperatingSystem.IsWindows() ? GpuBackend.Dx12 : GpuBackend.Vulkan;
    }
}

/// <summary>
/// The cooked shader blobs (<c>scripts/cook_shaders.py</c>): <c>name.hlsl.ENTRY.dxil</c> for DX12, <c>.spv</c> for
/// Vulkan, in <c>shaders/cooked</c> under the asset root (<see cref="AssetPaths"/>).
/// </summary>
public static class CookedShaders
{
    /// <summary>Load a cooked VS / PS pair for <paramref name="backend"/>.</summary>
    public static (byte[] Vs, byte[] Ps) Load(GpuBackend backend, string name)
    {
        return (LoadStage(backend, name, "VSMain"), LoadStage(backend, name, "PSMain"));
    }

    /// <summary>Load a single cooked stage.</summary>
    public static byte[] LoadStage(GpuBackend backend, string name, string entry)
    {
        string ext = backend == GpuBackend.Dx12 ? "dxil" : "spv";
        string file = $"{name}.hlsl.{entry}.{ext}";
        string? dir = AssetPaths.FindDirectory("shaders", "cooked") ?? throw new FileNotFoundException("cooked shaders not found - run scripts/cook_shaders.py (need " + file + "); " + AssetPaths.NotFoundMessage("shaders/cooked"));

        string path = Path.Combine(dir, file);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("cooked shader missing: " + path + " - run scripts/cook_shaders.py (with --dxc for DXIL)", path);
        }

        return File.ReadAllBytes(path);
    }
}
