using System;
using System.Numerics;

namespace MewStage.Demo;

/// <summary>The cube color presets the Overlay pause menu and the Workbench inspector offer (tints over the authored face colors).</summary>
internal static class CubeColors
{
    /// <summary>The preset names, in ComboBox order.</summary>
    public static readonly string[] Names = ["As authored", "Warm", "Cool", "Mono"];

    private static readonly Vector3[] Tints =
    [
        Vector3.One,
        new(1.15f, 0.85f, 0.65f),
        new(0.65f, 0.85f, 1.2f),
        new(0.8f, 0.8f, 0.8f),
    ];

    /// <summary>The tint of preset <paramref name="index"/> (clamped).</summary>
    public static Vector3 Tint(int index)
    {
        return Tints[Math.Clamp(index, 0, Tints.Length - 1)];
    }
}
