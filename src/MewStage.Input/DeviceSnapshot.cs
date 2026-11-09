using System;
using System.Diagnostics.CodeAnalysis;

namespace MewStage.Input;

/// <summary>
/// One connected controller's state for one frame: a plain data holder the capture layer fills and the UI reads.
/// The primary controller is modeled by the gamepad fields on <see cref="RawInputSnapshot"/>; every further one is
/// an entry in <see cref="RawInputSnapshot.Devices"/>. Arrays are fixed size and reused every frame (no per-frame
/// allocation), which is why they are exposed as arrays rather than copied out.
/// </summary>
public sealed class DeviceSnapshot
{
    /// <summary>The device is plugged in and readable this frame.</summary>
    public bool Connected { get; set; }

    /// <summary>Axis values indexed by <see cref="GamepadAxis"/>: sticks -1..1, triggers 0..1.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Fixed-size per-frame state written in place by the capture layer; copying would allocate every frame.")]
    public float[] Axes { get; } = new float[(int)GamepadAxis.Count];

    /// <summary>Button level state indexed by <see cref="GamepadButton"/>.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Fixed-size per-frame state written in place by the capture layer; copying would allocate every frame.")]
    public bool[] Buttons { get; } = new bool[(int)GamepadButton.Count];

    /// <summary>Friendly device name (for a settings page); empty when unknown.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Stable per-model device key (the SDL GUID string), for per-device persistence; empty when unknown.</summary>
    public string DeviceGuid { get; set; } = string.Empty;

    /// <summary>Which class of device this is.</summary>
    public DeviceKind Kind { get; set; } = DeviceKind.Gamepad;

    /// <summary>Reads one axis.</summary>
    /// <param name="a">The axis.</param>
    /// <returns>The value.</returns>
    public float Axis(GamepadAxis a)
    {
        return Axes[(int)a];
    }

    /// <summary>Reads one button's level state.</summary>
    /// <param name="b">The button.</param>
    /// <returns>True while held.</returns>
    public bool IsButtonDown(GamepadButton b)
    {
        return Buttons[(int)b];
    }

    /// <summary>Resets the state to disconnected, every axis and button cleared.</summary>
    public void Clear()
    {
        Connected = false;
        Array.Clear(Axes);
        Array.Clear(Buttons);
    }
}
