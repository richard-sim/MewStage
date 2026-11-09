using System;

using MewStage.Input;

using SDL;

using Xunit;

namespace MewStage.Host.Tests.Input;

/// <summary>
/// The gamepad capture's device bookkeeping without SDL devices: the first pad is the snapshot's primary, the second
/// goes to Devices[0], axes are normalized to -1..1, SDL's button names map to the device-agnostic ones, and a removal
/// clears the slot it held.
/// </summary>
public sealed class SdlGamepadCaptureTests
{
    [Fact]
    public void FirstPad_IsThePrimary_SecondPad_IsDevicesZero()
    {
        using var cap = new SdlGamepadCapture();
        var input = new RawInputSnapshot();
        cap.AddForTest(11, "Pad One", "guid-1");
        cap.ResyncForTest(input);
        Assert.True(input.GamepadConnected);
        Assert.Equal("Pad One", input.GamepadName);
        Assert.Equal("guid-1", input.GamepadGuid);
        Assert.Equal(DeviceKind.Gamepad, input.GamepadKind);
        Assert.Empty(input.Devices);

        cap.AddForTest(22, "Pad Two", "guid-2");
        cap.ResyncForTest(input);
        Assert.Equal(2, cap.DeviceCount);
        Assert.Single(input.Devices);
        Assert.True(input.Devices[0].Connected);
        Assert.Equal("Pad Two", input.Devices[0].Name);
        Assert.Equal("guid-2", input.Devices[0].DeviceGuid);

        // Input routes by instance id: the primary's into the snapshot's arrays, the second's into its device.
        cap.ApplyAxis(input, 11, (int)GamepadAxis.LeftX, 16384);
        cap.ApplyAxis(input, 22, (int)GamepadAxis.LeftX, -32768);
        cap.ApplyButton(input, 22, (byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, true);
        Assert.Equal(0.5f, input.GamepadAxes[(int)GamepadAxis.LeftX], 3);
        Assert.Equal(-1f, input.Devices[0].Axes[(int)GamepadAxis.LeftX]);
        Assert.True(input.Devices[0].Buttons[(int)GamepadButton.A]);
        Assert.False(input.GamepadButtonDown[(int)GamepadButton.A]);

        // An unknown instance id, an axis past the enum and an unmapped button are ignored.
        cap.ApplyAxis(input, 99, 0, 32767);
        cap.ApplyAxis(input, 11, (int)GamepadAxis.Count, 32767);
        cap.ApplyButton(input, 11, 250, true);
        Assert.Equal(0.5f, input.GamepadAxes[(int)GamepadAxis.LeftX], 3);
        Assert.DoesNotContain(true, input.GamepadButtonDown);
    }

    [Fact]
    public void Removal_ClearsTheReleasedSlot()
    {
        using var cap = new SdlGamepadCapture();
        var input = new RawInputSnapshot();
        cap.AddForTest(11, "Pad One", "guid-1");
        cap.AddForTest(22, "Pad Two", "guid-2");
        cap.ResyncForTest(input);
        cap.ApplyButton(input, 22, (byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, true);
        Assert.True(input.Devices[0].Buttons[(int)GamepadButton.B]);

        // The second pad leaves: its device slot is disconnected and released, and the primary is untouched.
        SDL_Event e = default;
        e.type = (uint)SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED;
        e.gdevice.which = (SDL_JoystickID)22;
        Assert.True(cap.TryHandleEvent(e, input));
        Assert.Equal(1, cap.DeviceCount);
        Assert.True(input.GamepadConnected);
        Assert.False(input.Devices[0].Connected);
        Assert.False(input.Devices[0].Buttons[(int)GamepadButton.B]);

        // The primary leaves: nothing is connected and its level state is released.
        input.GamepadButtonDown[(int)GamepadButton.A] = true;
        e.gdevice.which = (SDL_JoystickID)11;
        Assert.True(cap.TryHandleEvent(e, input));
        Assert.False(input.GamepadConnected);
        Assert.Equal(string.Empty, input.GamepadName);
        Assert.DoesNotContain(true, input.GamepadButtonDown);
    }

    [Fact]
    public void ButtonMap_CoversTheNavigatorsButtons_AndNothingElse()
    {
        Assert.Equal((int)GamepadButton.A, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH));
        Assert.Equal((int)GamepadButton.B, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST));
        Assert.Equal((int)GamepadButton.X, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST));
        Assert.Equal((int)GamepadButton.Y, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH));
        Assert.Equal((int)GamepadButton.LeftBumper, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER));
        Assert.Equal((int)GamepadButton.RightBumper, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER));
        Assert.Equal((int)GamepadButton.Start, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START));
        Assert.Equal((int)GamepadButton.Back, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK));
        Assert.Equal((int)GamepadButton.DPadUp, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP));
        Assert.Equal((int)GamepadButton.DPadDown, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN));
        Assert.Equal((int)GamepadButton.DPadLeft, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT));
        Assert.Equal((int)GamepadButton.DPadRight, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT));
        Assert.Equal(-1, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE));
        Assert.Equal(-1, SdlGamepadCapture.MapGamepadButton((byte)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK));

        // Every GamepadButton is reachable from an SDL button.
        foreach (GamepadButton b in Enum.GetValues<GamepadButton>())
        {
            if (b == GamepadButton.Count)
            {
                continue;
            }

            bool reachable = false;
            for (int i = 0; i < (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_COUNT; i++)
            {
                if (SdlGamepadCapture.MapGamepadButton((byte)i) == (int)b)
                {
                    reachable = true;
                    break;
                }
            }

            Assert.True(reachable, $"no SDL button maps to {b}");
        }
    }

    [Fact]
    public void JoystickEvents_AreConsumedWithoutEffect()
    {
        using var cap = new SdlGamepadCapture();
        var input = new RawInputSnapshot();
        SDL_Event e = default;
        e.type = (uint)SDL_EventType.SDL_EVENT_JOYSTICK_AXIS_MOTION;
        Assert.True(cap.TryHandleEvent(e, input));
        e.type = (uint)SDL_EventType.SDL_EVENT_KEY_DOWN;
        Assert.False(cap.TryHandleEvent(e, input));
        Assert.False(input.GamepadConnected);
    }
}
