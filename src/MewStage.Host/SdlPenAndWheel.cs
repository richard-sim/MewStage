// SDL3 pen events and the horizontal wheel into RawInputSnapshot. ONE law: the snapshot is the only input state; this is
// the SDL translation for it, called by AppHost's pump (an application with its own pump can call it the same way).
// Axis ranges are SDL's (https://wiki.libsdl.org/SDL3/SDL_PenAxis): pressure 0..1, tilt -90..90 degrees, distance
// 0..1, rotation -180..179.9 degrees.
using System;

using MewStage.Input;

using SDL;

namespace MewStage.Host;

/// <summary>Pen + horizontal-wheel SDL translation (see the file header).</summary>
public static class SdlPenAndWheel
{
    /// <summary>SDL pen buttons tracked (SDL_PenInputFlags has BUTTON_1..BUTTON_5).</summary>
    public const int MaxPenButtons = 5;

    /// <summary>
    /// Applies one SDL event to <paramref name="input"/>. Pen events are consumed (true); the mouse wheel adds its X to
    /// <see cref="RawInputSnapshot.MouseWheelX"/> and is NOT consumed (the pump still adds its Y); everything else is
    /// ignored (false).
    /// </summary>
    public static bool Apply(in SDL_Event e, RawInputSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(input);
        switch ((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                input.MouseWheelX += e.wheel.x;
                return false;
            case SDL_EventType.SDL_EVENT_PEN_PROXIMITY_IN:
                Touch(input, (uint)e.pproximity.which);
                input.PenInProximity = true;
                return true;
            case SDL_EventType.SDL_EVENT_PEN_PROXIMITY_OUT:
                Touch(input, (uint)e.pproximity.which);
                input.PenInProximity = false;
                input.PenDown = false;
                input.PenButtons = 0;
                input.PenPressure = 0f;
                return true;
            case SDL_EventType.SDL_EVENT_PEN_DOWN:
            case SDL_EventType.SDL_EVENT_PEN_UP:
                Touch(input, (uint)e.ptouch.which);
                input.PenX = e.ptouch.x;
                input.PenY = e.ptouch.y;
                input.PenDown = e.ptouch.down;
                input.PenEraser = e.ptouch.eraser;
                input.PenInProximity = true;
                if (!input.PenDown)
                {
                    input.PenPressure = 0f;
                }

                return true;
            case SDL_EventType.SDL_EVENT_PEN_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_PEN_BUTTON_UP:
                ApplyButton(in e, input);
                return true;
            case SDL_EventType.SDL_EVENT_PEN_MOTION:
                Touch(input, (uint)e.pmotion.which);
                input.PenX = e.pmotion.x;
                input.PenY = e.pmotion.y;
                return true;
            case SDL_EventType.SDL_EVENT_PEN_AXIS:
                ApplyAxis(in e, input);
                return true;
            default:
                return false;
        }
    }

    private static void ApplyButton(in SDL_Event e, RawInputSnapshot input)
    {
        Touch(input, (uint)e.pbutton.which);
        input.PenX = e.pbutton.x;
        input.PenY = e.pbutton.y;
        int b = e.pbutton.button;
        if (b is >= 1 and <= MaxPenButtons)
        {
            uint bit = 1u << (b - 1);
            input.PenButtons = e.pbutton.down ? input.PenButtons | bit : input.PenButtons & ~bit;
        }
    }

    private static void ApplyAxis(in SDL_Event e, RawInputSnapshot input)
    {
        Touch(input, (uint)e.paxis.which);
        input.PenX = e.paxis.x;
        input.PenY = e.paxis.y;
        switch (e.paxis.axis)
        {
            case SDL_PenAxis.SDL_PEN_AXIS_PRESSURE:
                input.PenPressure = e.paxis.value;
                break;
            case SDL_PenAxis.SDL_PEN_AXIS_XTILT:
                input.PenTiltX = e.paxis.value;
                break;
            case SDL_PenAxis.SDL_PEN_AXIS_YTILT:
                input.PenTiltY = e.paxis.value;
                break;
            case SDL_PenAxis.SDL_PEN_AXIS_DISTANCE:
                input.PenDistance = e.paxis.value;
                break;
            case SDL_PenAxis.SDL_PEN_AXIS_ROTATION:
                input.PenRotation = e.paxis.value;
                break;
            default:
                break;   // the slider and tangential-pressure axes are not carried by the snapshot
        }
    }

    private static void Touch(RawInputSnapshot input, uint id)
    {
        input.PenId = id;
        input.PenEvents++;
    }
}
