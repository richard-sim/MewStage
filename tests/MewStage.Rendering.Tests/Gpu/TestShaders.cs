using System.IO;
using System.Reflection;

namespace MewStage.Rendering.Tests.Gpu;

/// <summary>The cooked test fixtures embedded in this assembly (tests/MewStage.Rendering.Tests/Shaders/cooked).
/// Re-cook with <c>python scripts/cook_shaders.py --dxc &lt;dxc&gt; --shaders tests/MewStage.Rendering.Tests/Shaders</c>.</summary>
public static class TestShaders
{
    /// <summary>Loads one cooked blob by its file name, e.g. <c>triangle.hlsl.VSMain.spv</c>.</summary>
    /// <param name="name">The blob's file name.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Load(string name)
    {
        using Stream? s = typeof(TestShaders).GetTypeInfo().Assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException("embedded test shader missing: " + name + " (re-cook the test fixtures)");

        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>The triangle fixture's vertex + pixel blobs for a backend.</summary>
    /// <param name="spirv">True for SPIR-V (Vulkan), false for DXIL (DX12).</param>
    /// <returns>Vertex and pixel shader bytes.</returns>
    public static (byte[] Vs, byte[] Ps) Triangle(bool spirv)
    {
        string ext = spirv ? "spv" : "dxil";
        return (Load("triangle.hlsl.VSMain." + ext), Load("triangle.hlsl.PSMain." + ext));
    }
}
