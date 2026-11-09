using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace MewStage.Input;

/// <summary>
/// Raw, device-agnostic input and window state for ONE frame. The host (the SDL3 window in <c>MewStage.Host</c>)
/// fills it while pumping events; the UI bridge, the gamepad navigator and the app's own input code read it. Nothing
/// here references SDL or any windowing library: this class is the seam between them.
/// </summary>
/// <remarks>
/// Keys and buttons are LEVEL state (is-down this frame); mouse motion, wheel and typed text are per-frame deltas
/// that <see cref="ClearDeltas"/> resets. The arrays are fixed size and reused every frame, so a steady-state frame
/// allocates nothing. The UI bridge turns level state into the edge events MewUI wants by keeping a one-frame shadow.
/// </remarks>
public sealed class RawInputSnapshot
{
    /// <summary>Chars of typed text one frame holds: far above a frame of typing or a pasted word.</summary>
    public const int TextInputCapacity = 256;

    /// <summary>Key level state indexed by <see cref="KeyCode"/>.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Fixed-size per-frame state written in place by the host; copying would allocate every frame.")]
    public bool[] KeyDown { get; } = new bool[(int)KeyCode.Count];

    /// <summary>Mouse button level state indexed by <see cref="MouseButton"/>.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Fixed-size per-frame state written in place by the host; copying would allocate every frame.")]
    public bool[] MouseButtonDown { get; } = new bool[(int)MouseButton.Count];

    /// <summary>Cursor X in window pixels.</summary>
    public float MouseX { get; set; }

    /// <summary>Cursor Y in window pixels (top is 0).</summary>
    public float MouseY { get; set; }

    /// <summary>Cursor motion since the last frame, X (pixels).</summary>
    public float MouseDeltaX { get; set; }

    /// <summary>Cursor motion since the last frame, Y (pixels).</summary>
    public float MouseDeltaY { get; set; }

    /// <summary>Vertical wheel notches this frame; positive = scrolled up / away from the user.</summary>
    public float MouseWheel { get; set; }

    /// <summary>Horizontal wheel notches this frame; positive = scrolled right (the SDL convention). 0 on hosts that
    /// do not pump it.</summary>
    public float MouseWheelX { get; set; }

    /// <summary>Id of the pen that sent the last pen event (0 = none yet). Pen fields are level state of that pen;
    /// hosts without tablets leave them all default.</summary>
    public uint PenId { get; set; }

    /// <summary>The pen is near the tablet.</summary>
    public bool PenInProximity { get; set; }

    /// <summary>The tip (or the eraser end) touches.</summary>
    public bool PenDown { get; set; }

    /// <summary>The touching end is the eraser.</summary>
    public bool PenEraser { get; set; }

    /// <summary>Barrel buttons down: bit N = pen button N + 1.</summary>
    public uint PenButtons { get; set; }

    /// <summary>Pen X in window pixels.</summary>
    public float PenX { get; set; }

    /// <summary>Pen Y in window pixels.</summary>
    public float PenY { get; set; }

    /// <summary>Tip pressure 0..1 (0 while lifted).</summary>
    public float PenPressure { get; set; }

    /// <summary>Tilt about the tablet's X axis, degrees -90..90.</summary>
    public float PenTiltX { get; set; }

    /// <summary>Tilt about the tablet's Y axis, degrees -90..90.</summary>
    public float PenTiltY { get; set; }

    /// <summary>Hover distance 0..1.</summary>
    public float PenDistance { get; set; }

    /// <summary>Barrel rotation, degrees.</summary>
    public float PenRotation { get; set; }

    /// <summary>Pen events received this frame (a delta).</summary>
    public int PenEvents { get; set; }

    /// <summary>The primary controller is connected.</summary>
    public bool GamepadConnected { get; set; }

    /// <summary>Primary controller axes indexed by <see cref="GamepadAxis"/>: sticks -1..1, triggers 0..1.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Fixed-size per-frame state written in place by the host; copying would allocate every frame.")]
    public float[] GamepadAxes { get; } = new float[(int)GamepadAxis.Count];

    /// <summary>Primary controller button level state indexed by <see cref="GamepadButton"/>.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Fixed-size per-frame state written in place by the host; copying would allocate every frame.")]
    public bool[] GamepadButtonDown { get; } = new bool[(int)GamepadButton.Count];

    /// <summary>Primary controller's friendly name; empty when unknown.</summary>
    public string GamepadName { get; set; } = string.Empty;

    /// <summary>Primary controller's stable per-model key (the SDL GUID string); empty when unknown.</summary>
    public string GamepadGuid { get; set; } = string.Empty;

    /// <summary>Primary controller's device class.</summary>
    public DeviceKind GamepadKind { get; set; } = DeviceKind.Gamepad;

    /// <summary>Further controllers beyond the primary: <c>Devices[i]</c> is the (i + 1)-th physical device. The
    /// host fills the list as it enumerates controllers; empty when there is only one or none.</summary>
    public List<DeviceSnapshot> Devices { get; } = [];

    /// <summary>Window client width in pixels.</summary>
    public int WindowWidth { get; set; } = 1280;

    /// <summary>Window client height in pixels.</summary>
    public int WindowHeight { get; set; } = 720;

    /// <summary>The window has keyboard focus.</summary>
    public bool Focused { get; set; } = true;

    /// <summary>The user asked to close the window (close button, Alt+F4, quit).</summary>
    public bool CloseRequested { get; set; }

    /// <summary>Set by the UI layer when a UI element owns the keyboard, so app-level key handling (camera keys,
    /// shortcuts) can stand down.</summary>
    public bool UiWantsKeyboard { get; set; }

    /// <summary>Set by the UI layer when the pointer is over a UI element that takes input, so app-level mouse
    /// handling (orbit, pick) can stand down.</summary>
    public bool UiWantsMouse { get; set; }

    /// <summary>Text typed this frame (UTF-16, the OS text-input events with layout and IME applied), for a focused
    /// text field. Fixed capacity; text past the capacity in one frame is dropped. The UI bridge consumes it (sets
    /// <see cref="TextInputLength"/> to 0) when it delivers it; <see cref="ClearDeltas"/> also resets it.</summary>
    [SuppressMessage("Performance", "CA1819:Properties should not return arrays", Justification = "Fixed-size per-frame buffer written in place by the host; copying would allocate every frame.")]
    public char[] TextInput { get; } = new char[TextInputCapacity];

    /// <summary>Number of valid leading chars in <see cref="TextInput"/> this frame.</summary>
    public int TextInputLength { get; set; }

    /// <summary>Whether a key is held.</summary>
    /// <param name="k">The key.</param>
    /// <returns>True while held; false for <see cref="KeyCode.None"/> and out-of-range values.</returns>
    public bool IsKeyDown(KeyCode k)
    {
        return k > KeyCode.None && (int)k < KeyDown.Length && KeyDown[(int)k];
    }

    /// <summary>Whether a mouse button is held.</summary>
    /// <param name="b">The button.</param>
    /// <returns>True while held.</returns>
    public bool IsMouseDown(MouseButton b)
    {
        return MouseButtonDown[(int)b];
    }

    /// <summary>Reads one primary-controller axis.</summary>
    /// <param name="a">The axis.</param>
    /// <returns>The value.</returns>
    public float Axis(GamepadAxis a)
    {
        return GamepadAxes[(int)a];
    }

    /// <summary>Whether a primary-controller button is held.</summary>
    /// <param name="b">The button.</param>
    /// <returns>True while held.</returns>
    public bool IsButtonDown(GamepadButton b)
    {
        return GamepadButtonDown[(int)b];
    }

    /// <summary>Appends typed text; whatever does not fit in <see cref="TextInputCapacity"/> this frame is dropped.</summary>
    /// <param name="text">The typed chars.</param>
    public void AppendTextInput(ReadOnlySpan<char> text)
    {
        int n = Math.Min(text.Length, TextInputCapacity - TextInputLength);
        if (n <= 0)
        {
            return;
        }

        text[..n].CopyTo(TextInput.AsSpan(TextInputLength));
        TextInputLength += n;
    }

    /// <summary>Resets the per-frame deltas (motion, wheel, pen events, typed text). Call after the frame's input has
    /// been consumed; level state is left alone.</summary>
    public void ClearDeltas()
    {
        MouseDeltaX = 0f;
        MouseDeltaY = 0f;
        MouseWheel = 0f;
        MouseWheelX = 0f;
        PenEvents = 0;
        TextInputLength = 0;
    }
}
