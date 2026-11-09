namespace MewStage.Input;

/// <summary>Mouse buttons the snapshot tracks.</summary>
public enum MouseButton
{
    /// <summary>Left button.</summary>
    Left = 0,

    /// <summary>Right button.</summary>
    Right = 1,

    /// <summary>Middle button (wheel click).</summary>
    Middle = 2,

    /// <summary>One past the last button; sizes the per-button arrays.</summary>
    Count = 3,
}

/// <summary>Mapped gamepad axes. Sticks read -1..1, triggers 0..1.</summary>
public enum GamepadAxis
{
    /// <summary>Left stick X (right positive).</summary>
    LeftX = 0,

    /// <summary>Left stick Y (down positive, the SDL convention).</summary>
    LeftY = 1,

    /// <summary>Right stick X.</summary>
    RightX = 2,

    /// <summary>Right stick Y.</summary>
    RightY = 3,

    /// <summary>Left trigger, 0..1.</summary>
    LeftTrigger = 4,

    /// <summary>Right trigger, 0..1.</summary>
    RightTrigger = 5,

    /// <summary>One past the last axis; sizes the per-axis arrays.</summary>
    Count = 6,
}

/// <summary>Mapped gamepad buttons (Xbox naming: A is the south face button).</summary>
public enum GamepadButton
{
    /// <summary>South face button (confirm).</summary>
    A = 0,

    /// <summary>East face button (back / cancel).</summary>
    B = 1,

    /// <summary>West face button.</summary>
    X = 2,

    /// <summary>North face button.</summary>
    Y = 3,

    /// <summary>Left shoulder.</summary>
    LeftBumper = 4,

    /// <summary>Right shoulder.</summary>
    RightBumper = 5,

    /// <summary>Start / menu.</summary>
    Start = 6,

    /// <summary>Back / view / select.</summary>
    Back = 7,

    /// <summary>D-pad up.</summary>
    DPadUp = 8,

    /// <summary>D-pad down.</summary>
    DPadDown = 9,

    /// <summary>D-pad left.</summary>
    DPadLeft = 10,

    /// <summary>D-pad right.</summary>
    DPadRight = 11,

    /// <summary>One past the last button; sizes the per-button arrays.</summary>
    Count = 12,
}

/// <summary>The class of physical device behind a <see cref="DeviceSnapshot"/>. The capture layer maps its backend
/// to this; nothing in <c>MewStage.Input</c> references SDL.</summary>
public enum DeviceKind
{
    /// <summary>A controller with a known mapping: the fixed <see cref="GamepadAxis"/> / <see cref="GamepadButton"/>
    /// arrays are filled. The gamepad navigator reads devices of this kind.</summary>
    Gamepad = 0,

    /// <summary>A joystick without a gamepad mapping; reported for completeness, not navigated with.</summary>
    Joystick = 1,
}
