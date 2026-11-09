using System;

namespace MewStage.Graphics.Diagnostics;

/// <summary>ARGB colors for GPU debug regions and profiler zones.</summary>
public static class EventColor
{
    /// <summary>Let the tool pick a color.</summary>
    public const uint Auto = 0u;

    /// <summary>Rendering / GPU zones (green).</summary>
    public const uint Render = 0xFF30C030u;

    /// <summary>UI zones (teal).</summary>
    public const uint Ui = 0xFF20A0A0u;

    /// <summary>Scheduler and frame-boundary zones (gray).</summary>
    public const uint Frame = 0xFF808080u;

    /// <summary>Composes an opaque ARGB color from 8-bit components.</summary>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <returns>The ARGB value.</returns>
    public static uint Rgb(byte r, byte g, byte b)
    {
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    /// <summary>Unpacks an ARGB color to float RGBA in [0, 1] (Vulkan <c>VkDebugUtilsLabelEXT.color</c>).</summary>
    /// <param name="argb">The ARGB color.</param>
    /// <param name="rgba">Receives at least four floats.</param>
    public static void ToRgbaFloat(uint argb, Span<float> rgba)
    {
        if (rgba.Length < 4)
        {
            throw new ArgumentException("rgba must have length >= 4", nameof(rgba));
        }

        rgba[0] = ((argb >> 16) & 0xFF) / 255f;
        rgba[1] = ((argb >> 8) & 0xFF) / 255f;
        rgba[2] = (argb & 0xFF) / 255f;
        rgba[3] = ((argb >> 24) & 0xFF) / 255f;
    }
}
