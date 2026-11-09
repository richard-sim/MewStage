// SDL3 gamepad capture: owns device enumeration, open and close, and fills the device-agnostic RawInputSnapshot
// (MewStage.Input never references SDL). Only MAPPED gamepads are opened (SDL_OpenGamepad: a device SDL has a button
// and axis mapping for); a joystick SDL does not recognize as a gamepad is left alone. Device order is kept from the
// connection order: device 0 is the primary and maps to the snapshot's gamepad fields, device k >= 1 to Devices[k - 1],
// so a second pad can drive the same UI (the navigator reads every connected gamepad-kind device).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using MewStage.Input;

using SDL;

using static SDL.SDL3;

namespace MewStage.Host;

/// <summary>Gamepad capture through SDL3 (see the file header). Plug <see cref="TryHandleEvent"/> into the pump.</summary>
public sealed unsafe class SdlGamepadCapture : IDisposable
{
    /// <summary>SDL_Init flags this capture layer needs. A host that also needs video ORs them in.</summary>
    public const SDL_InitFlags InitFlags = SDL_InitFlags.SDL_INIT_GAMEPAD | SDL_InitFlags.SDL_INIT_JOYSTICK;

    private sealed class Entry
    {
        public uint Which { get; init; }            // SDL_JoystickID (stable instance id while connected)

        public nint Handle { get; init; }           // SDL_Gamepad*

        public string Name { get; init; } = string.Empty;

        public string Guid { get; init; } = string.Empty;
    }

    // Ordered device list. Index 0 = primary (the snapshot's gamepad fields); index k >= 1 = Devices[k - 1].
    private readonly List<Entry> _devices = [];

    /// <summary>Live count of SDL gamepads this layer currently tracks (primary + Devices[]).</summary>
    public int DeviceCount => _devices.Count;

    /// <summary>
    /// Handle a controller-related SDL event (add / remove / axis / button), filling <paramref name="input"/>. Returns
    /// true if the event was a controller event this layer consumed, false otherwise (the host falls through to its own
    /// window, keyboard and mouse handling). The host keeps its own SDL_PollEvent loop and forwards each event here.
    /// </summary>
    public bool TryHandleEvent(in SDL_Event e, RawInputSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(input);
        switch ((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED:
                Add((uint)e.gdevice.which, e.gdevice.which);
                Resync(input);
                return true;
            case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED:
                RemoveWhich((uint)e.gdevice.which);
                Resync(input);
                return true;
            case SDL_EventType.SDL_EVENT_GAMEPAD_AXIS_MOTION:
                ApplyAxis(input, (uint)e.gaxis.which, e.gaxis.axis, e.gaxis.value);
                return true;
            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_GAMEPAD_BUTTON_UP:
                ApplyButton(input, (uint)e.gbutton.which, e.gbutton.button, e.gbutton.down);
                return true;
            case SDL_EventType.SDL_EVENT_JOYSTICK_ADDED:
            case SDL_EventType.SDL_EVENT_JOYSTICK_REMOVED:
            case SDL_EventType.SDL_EVENT_JOYSTICK_AXIS_MOTION:
            case SDL_EventType.SDL_EVENT_JOYSTICK_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_JOYSTICK_BUTTON_UP:
            case SDL_EventType.SDL_EVENT_JOYSTICK_HAT_MOTION:
            case SDL_EventType.SDL_EVENT_JOYSTICK_BALL_MOTION:
                return true;   // the joystick echo of a gamepad, or a device without a mapping: consumed, not used
            default:
                return false;
        }
    }

    /// <summary>Apply one gamepad axis value (SDL's -32768..32767) to the device the instance id names.</summary>
    internal void ApplyAxis(RawInputSnapshot input, uint which, int axis, short value)
    {
        int di = IndexOfWhich(which);
        if (di < 0 || axis < 0 || axis >= (int)GamepadAxis.Count)
        {
            return;
        }

        float v = Math.Clamp(value / 32767f, -1f, 1f);   // SDL gamepad axes 0..5 align with GamepadAxis
        if (di == 0)
        {
            input.GamepadAxes[axis] = v;
        }
        else
        {
            DeviceFor(input, di).Axes[axis] = v;
        }
    }

    /// <summary>Apply one gamepad button edge to the device the instance id names.</summary>
    internal void ApplyButton(RawInputSnapshot input, uint which, byte button, bool down)
    {
        int di = IndexOfWhich(which);
        int gb = MapGamepadButton(button);
        if (di < 0 || gb < 0)
        {
            return;
        }

        if (di == 0)
        {
            input.GamepadButtonDown[gb] = down;
        }
        else
        {
            DeviceFor(input, di).Buttons[gb] = down;
        }
    }

    /// <summary>Map an SDL3 gamepad button to the device-agnostic <see cref="GamepadButton"/>, or -1.</summary>
    public static int MapGamepadButton(byte b)
    {
        return (SDL_GamepadButton)b switch
        {
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH => (int)GamepadButton.A,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST => (int)GamepadButton.B,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST => (int)GamepadButton.X,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH => (int)GamepadButton.Y,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER => (int)GamepadButton.LeftBumper,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER => (int)GamepadButton.RightBumper,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START => (int)GamepadButton.Start,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK => (int)GamepadButton.Back,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP => (int)GamepadButton.DPadUp,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN => (int)GamepadButton.DPadDown,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT => (int)GamepadButton.DPadLeft,
            SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT => (int)GamepadButton.DPadRight,
            _ => -1,
        };
    }

    // Test seam: track a device without SDL (its handle is 0, so Dispose closes nothing).
    internal void AddForTest(uint which, string name, string guid)
    {
        _devices.Add(new Entry { Which = which, Handle = 0, Name = name, Guid = guid });
    }

    // Test seam: the snapshot reconciliation after a connect / remove.
    internal void ResyncForTest(RawInputSnapshot input)
    {
        Resync(input);
    }

    private void Add(uint which, SDL_JoystickID id)
    {
        if (IndexOfWhich(which) >= 0)
        {
            return;
        }

        SDL_Gamepad* pad = SDL_OpenGamepad(id);
        if (pad == null)
        {
            return;
        }

        string name = SDL_GetGamepadName(pad) ?? string.Empty;
        string guid = GuidString(id);
        _devices.Add(new Entry { Which = which, Handle = (nint)pad, Name = name, Guid = guid });
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[input] +gamepad '{name}' guid={guid}"));
    }

    private int IndexOfWhich(uint which)
    {
        for (int i = 0; i < _devices.Count; i++)
        {
            if (_devices[i].Which == which)
            {
                return i;
            }
        }

        return -1;
    }

    private void RemoveWhich(uint which)
    {
        int di = IndexOfWhich(which);
        if (di >= 0)
        {
            RemoveAt(di);
        }
    }

    private void RemoveAt(int di)
    {
        Entry e = _devices[di];
        if (e.Handle != 0)
        {
            SDL_CloseGamepad((SDL_Gamepad*)e.Handle);
        }

        _devices.RemoveAt(di);
    }

    // Reconcile the snapshot's identity and connected flags with the live device list after a connect or remove.
    private void Resync(RawInputSnapshot input)
    {
        int count = _devices.Count;
        input.GamepadConnected = count > 0;
        if (count > 0)
        {
            Entry d0 = _devices[0];
            input.GamepadName = d0.Name;
            input.GamepadGuid = d0.Guid;
            input.GamepadKind = DeviceKind.Gamepad;
        }
        else
        {
            input.GamepadName = string.Empty;
            input.GamepadGuid = string.Empty;
            input.GamepadKind = DeviceKind.Gamepad;
            Array.Clear(input.GamepadAxes);
            Array.Clear(input.GamepadButtonDown);
        }

        for (int i = 1; i < count; i++)
        {
            DeviceSnapshot d = DeviceFor(input, i);
            Entry src = _devices[i];
            d.Connected = true;
            d.Name = src.Name;
            d.DeviceGuid = src.Guid;
            d.Kind = DeviceKind.Gamepad;
        }

        // Mark any trailing snapshot devices (beyond the live count) disconnected and released.
        for (int i = Math.Max(0, count - 1); i < input.Devices.Count; i++)
        {
            input.Devices[i].Connected = false;
            input.Devices[i].Clear();
        }
    }

    // The DeviceSnapshot backing device index >= 1 (Devices[index - 1]); grows the list.
    private static DeviceSnapshot DeviceFor(RawInputSnapshot input, int deviceIndex)
    {
        int j = deviceIndex - 1;
        while (input.Devices.Count <= j)
        {
            input.Devices.Add(new DeviceSnapshot());
        }

        return input.Devices[j];
    }

    // The SDL GUID of an instance id as the canonical lowercase hex string (a stable per-model key).
    private static string GuidString(SDL_JoystickID id)
    {
        SDL_GUID g = SDL_GetJoystickGUIDForID(id);
        byte* buf = stackalloc byte[33];
        SDL_GUIDToString(g, buf, 33);
        int len = 0;
        while (len < 33 && buf[len] != 0)
        {
            len++;
        }

        return len == 0 ? string.Empty : Encoding.ASCII.GetString(buf, len);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        for (int i = _devices.Count - 1; i >= 0; i--)
        {
            RemoveAt(i);
        }
    }
}
