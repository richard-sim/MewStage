// The Overlay screen's state as observable values. The HUD binds the readouts (text that changes every frame: the
// retained-render point - a bound property set invalidates the window, the HUD never redraws on its own), the pause
// menu binds the settings two-way, and OverlayMode pushes the settings into the scene each frame.
using System;
using System.Globalization;
using System.Numerics;

using Aprillz.MewUI;

namespace MewStage.Demo.Overlay;

/// <summary>Observable state shared by the HUD, the pause menu and the scene (see the file header).</summary>
internal sealed class HudViewModel
{
    /// <summary>The cube color presets the pause menu's ComboBox offers (<see cref="CubeColors"/>).</summary>
    public static string[] ColorNames => CubeColors.Names;

    /// <summary>Last frame's CPU time, formatted.</summary>
    public ObservableValue<string> FrameTime { get; } = new("-- ms");

    /// <summary>The cube's spin angle, formatted.</summary>
    public ObservableValue<string> Angle { get; } = new("0 deg");

    /// <summary>Free text typed in the pause menu, shown on the HUD.</summary>
    public ObservableValue<string> Label { get; } = new("");

    /// <summary>Index into <see cref="ColorNames"/>.</summary>
    public ObservableValue<int> ColorIndex { get; } = new(0);

    /// <summary>Spin speed, radians per second (0..3).</summary>
    public ObservableValue<double> SpinSpeed { get; } = new(0.8);

    /// <summary>Floor grid on / off.</summary>
    public ObservableValue<bool> ShowGrid { get; } = new(true);

    /// <summary>The tint for <see cref="ColorIndex"/>.</summary>
    public Vector3 Tint => CubeColors.Tint(ColorIndex.Value);

    /// <summary>Update the per-frame readouts (only a changed string invalidates the HUD).</summary>
    public void SetReadouts(double frameMilliseconds, float angleRadians)
    {
        FrameTime.Set(frameMilliseconds.ToString("0.00", CultureInfo.InvariantCulture) + " ms");
        Angle.Set(((int)Math.Round(angleRadians * 180.0 / Math.PI)).ToString(CultureInfo.InvariantCulture) + " deg");
    }
}
