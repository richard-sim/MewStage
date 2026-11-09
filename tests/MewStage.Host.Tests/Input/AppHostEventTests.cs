using MewStage.Input;

using SDL;

using Xunit;

namespace MewStage.Host.Tests.Input;

/// <summary>
/// The SDL event pump's translation into the snapshot, event by event: keys are level state, mouse buttons map
/// 1 / 2 / 3 to Left / Middle / Right, the wheel accumulates both axes, motion sets the position and accumulates
/// deltas, focus loss releases everything held, pen events fill the pen fields, and quit / close report a close.
/// </summary>
public sealed unsafe class AppHostEventTests
{
    private static SDL_Event Event(SDL_EventType type)
    {
        SDL_Event e = default;
        e.type = (uint)type;
        return e;
    }

    [Fact]
    public void Keys_AreLevelState_ByScancode()
    {
        var input = new RawInputSnapshot();
        SDL_Event down = Event(SDL_EventType.SDL_EVENT_KEY_DOWN);
        down.key.scancode = SDL_Scancode.SDL_SCANCODE_SPACE;
        down.key.down = true;
        Assert.False(AppHost.ApplyEvent(down, input, null));
        Assert.True(input.IsKeyDown(KeyCode.Space));

        SDL_Event up = Event(SDL_EventType.SDL_EVENT_KEY_UP);
        up.key.scancode = SDL_Scancode.SDL_SCANCODE_SPACE;
        up.key.down = false;
        AppHost.ApplyEvent(up, input, null);
        Assert.False(input.IsKeyDown(KeyCode.Space));

        // An unmapped scancode changes nothing.
        SDL_Event odd = Event(SDL_EventType.SDL_EVENT_KEY_DOWN);
        odd.key.scancode = SDL_Scancode.SDL_SCANCODE_CAPSLOCK;
        odd.key.down = true;
        AppHost.ApplyEvent(odd, input, null);
        Assert.DoesNotContain(true, input.KeyDown);
    }

    [Fact]
    public void MouseButtons_MapSdlNumbering()
    {
        Assert.Equal((int)MouseButton.Left, AppHost.MapMouseButton(1));
        Assert.Equal((int)MouseButton.Middle, AppHost.MapMouseButton(2));
        Assert.Equal((int)MouseButton.Right, AppHost.MapMouseButton(3));
        Assert.Equal(-1, AppHost.MapMouseButton(4));
        Assert.Equal(-1, AppHost.MapMouseButton(0));

        var input = new RawInputSnapshot();
        SDL_Event e = Event(SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN);
        e.button.button = 3;
        e.button.down = true;
        AppHost.ApplyEvent(e, input, null);
        Assert.True(input.MouseButtonDown[(int)MouseButton.Right]);
        Assert.False(input.MouseButtonDown[(int)MouseButton.Left]);
        e.type = (uint)SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP;
        e.button.down = false;
        AppHost.ApplyEvent(e, input, null);
        Assert.False(input.MouseButtonDown[(int)MouseButton.Right]);
    }

    [Fact]
    public void Wheel_AccumulatesBothAxes_AcrossEvents()
    {
        var input = new RawInputSnapshot();
        SDL_Event e = Event(SDL_EventType.SDL_EVENT_MOUSE_WHEEL);
        e.wheel.y = 1f;
        e.wheel.x = -0.5f;
        AppHost.ApplyEvent(e, input, null);
        AppHost.ApplyEvent(e, input, null);
        Assert.Equal(2f, input.MouseWheel);
        Assert.Equal(-1f, input.MouseWheelX);
        input.ClearDeltas();
        Assert.Equal(0f, input.MouseWheel);
        Assert.Equal(0f, input.MouseWheelX);
    }

    [Fact]
    public void Motion_SetsPosition_AndAccumulatesDeltas()
    {
        var input = new RawInputSnapshot();
        SDL_Event e = Event(SDL_EventType.SDL_EVENT_MOUSE_MOTION);
        e.motion.x = 100f;
        e.motion.y = 50f;
        e.motion.xrel = 4f;
        e.motion.yrel = -2f;
        AppHost.ApplyEvent(e, input, null);
        e.motion.x = 103f;
        e.motion.xrel = 3f;
        e.motion.yrel = 0f;
        AppHost.ApplyEvent(e, input, null);
        Assert.Equal((103f, 50f), (input.MouseX, input.MouseY));
        Assert.Equal((7f, -2f), (input.MouseDeltaX, input.MouseDeltaY));
    }

    [Fact]
    public void FocusLost_ReleasesHeldKeysAndButtons()
    {
        var input = new RawInputSnapshot();
        input.KeyDown[(int)KeyCode.W] = true;
        input.MouseButtonDown[(int)MouseButton.Left] = true;
        Assert.False(AppHost.ApplyEvent(Event(SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST), input, null));
        Assert.DoesNotContain(true, input.KeyDown);
        Assert.DoesNotContain(true, input.MouseButtonDown);
        Assert.False(input.Focused);
        AppHost.ApplyEvent(Event(SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED), input, null);
        Assert.True(input.Focused);
    }

    [Fact]
    public void QuitAndClose_ReportAClose_OtherWindowEventsDoNot()
    {
        var input = new RawInputSnapshot();
        Assert.True(AppHost.ApplyEvent(Event(SDL_EventType.SDL_EVENT_QUIT), input, null));
        Assert.True(AppHost.ApplyEvent(Event(SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED), input, null));
        Assert.False(AppHost.ApplyEvent(Event(SDL_EventType.SDL_EVENT_WINDOW_SHOWN), input, null));
        Assert.False(AppHost.ApplyEvent(Event(SDL_EventType.SDL_EVENT_WINDOW_RESIZED), input, null));   // a null window: size kept
        Assert.Equal((1280, 720), (input.WindowWidth, input.WindowHeight));
    }

    [Fact]
    public void Pen_FillsThePenFields_AndIsConsumed()
    {
        var input = new RawInputSnapshot();
        SDL_Event prox = Event(SDL_EventType.SDL_EVENT_PEN_PROXIMITY_IN);
        prox.pproximity.which = (SDL_PenID)7;
        Assert.True(SdlPenAndWheel.Apply(prox, input));
        Assert.True(input.PenInProximity);
        Assert.Equal(7u, input.PenId);

        SDL_Event down = Event(SDL_EventType.SDL_EVENT_PEN_DOWN);
        down.ptouch.which = (SDL_PenID)7;
        down.ptouch.x = 10f;
        down.ptouch.y = 20f;
        down.ptouch.down = true;
        down.ptouch.eraser = true;
        Assert.True(SdlPenAndWheel.Apply(down, input));
        Assert.True(input.PenDown);
        Assert.True(input.PenEraser);
        Assert.Equal((10f, 20f), (input.PenX, input.PenY));

        SDL_Event axis = Event(SDL_EventType.SDL_EVENT_PEN_AXIS);
        axis.paxis.which = (SDL_PenID)7;
        axis.paxis.axis = SDL_PenAxis.SDL_PEN_AXIS_PRESSURE;
        axis.paxis.value = 0.75f;
        Assert.True(SdlPenAndWheel.Apply(axis, input));
        Assert.Equal(0.75f, input.PenPressure);

        SDL_Event button = Event(SDL_EventType.SDL_EVENT_PEN_BUTTON_DOWN);
        button.pbutton.which = (SDL_PenID)7;
        button.pbutton.button = 2;
        button.pbutton.down = true;
        Assert.True(SdlPenAndWheel.Apply(button, input));
        Assert.Equal(0b10u, input.PenButtons);

        SDL_Event outp = Event(SDL_EventType.SDL_EVENT_PEN_PROXIMITY_OUT);
        outp.pproximity.which = (SDL_PenID)7;
        Assert.True(SdlPenAndWheel.Apply(outp, input));
        Assert.False(input.PenInProximity);
        Assert.False(input.PenDown);
        Assert.Equal(0u, input.PenButtons);
        Assert.Equal(0f, input.PenPressure);
        Assert.Equal(5, input.PenEvents);

        // The same pen event through the host's translation is consumed before the mouse switch.
        Assert.False(AppHost.ApplyEvent(down, input, null));
        Assert.True(input.PenDown);
    }
}
