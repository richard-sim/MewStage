using System.IO;
using System.Reflection;

namespace MewStage.Ui.Tests.Support;

/// <summary>Embedded test inputs: the cooked production ui.hlsl (SPIR-V) and the Lora faces.</summary>
public static class TestAssets
{
    /// <summary>A dummy shader blob for the headless device, which ignores bytecode.</summary>
    public static byte[] DummyShader => [0, 0, 0, 0];

    /// <summary>The cooked ui.hlsl vertex + pixel SPIR-V (from assets/shaders/cooked).</summary>
    public static (byte[] Vs, byte[] Ps) UiShaders()
    {
        return (Load("ui.hlsl.VSMain.spv"), Load("ui.hlsl.PSMain.spv"));
    }

    /// <summary>One Lora face: <c>Lora-Regular.ttf</c>, <c>Lora-Bold.ttf</c>, <c>Lora-Italic.ttf</c> or <c>Lora-BoldItalic.ttf</c>.</summary>
    public static byte[] Lora(string style)
    {
        return Load("Lora-" + style + ".ttf");
    }

    private static byte[] Load(string name)
    {
        using Stream s = typeof(TestAssets).GetTypeInfo().Assembly.GetManifestResourceStream(name)
            ?? throw new FileNotFoundException("embedded test asset missing: " + name);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
