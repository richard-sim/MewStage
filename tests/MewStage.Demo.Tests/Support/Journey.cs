using System;
using System.Collections.Generic;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Host;
using MewStage.Input;

using SDL;

using static SDL.SDL3;

namespace MewStage.Demo.Tests.Support;

/// <summary>
/// One demo run offscreen on lavapipe: the real <see cref="DemoApp"/> (SDL offscreen window, headless Vulkan device,
/// offscreen swapchain, the frame loop, the modes), a readback of every presented frame, and input delivered the way
/// the app gets it: keys and the pointer as SDL events through the pump, typed text and gamepad buttons written into
/// the snapshot after the pump (the capture hooks write there too).
/// </summary>
internal sealed unsafe class Journey : IDisposable
{
    private readonly List<Action<RawInputSnapshot>> _oneFrame = [];
    private readonly List<Action<RawInputSnapshot>> _everyFrame = [];

    public Journey(DemoMode mode, int width = 1280, int height = 720)
    {
        var options = new DemoOptions { Mode = mode, Backend = GpuBackend.Vulkan };
        App = new DemoApp(options, offscreen: true, width, height);
        Readback = new FrameReadback(App.Host.Gpu.Device);
        App.Host.BeforePresent = Readback.Record;
        Action<RawInputSnapshot>? inner = App.Host.AfterPump;
        App.Host.AfterPump = input =>
        {
            foreach (Action<RawInputSnapshot> a in _everyFrame)
            {
                a(input);
            }

            foreach (Action<RawInputSnapshot> a in _oneFrame)
            {
                a(input);
            }

            _oneFrame.Clear();
            inner?.Invoke(input);
        };
    }

    /// <summary>The app under test.</summary>
    public DemoApp App { get; }

    /// <summary>The last presented frame.</summary>
    public FrameReadback Readback { get; }

    /// <summary>The SDL window id events are addressed to.</summary>
    public uint WindowId => App.Host.Gpu.WindowId;

    /// <summary>The design-fit scale (pixels per design DIP) of the current UI space, or the display scale in DIP space.</summary>
    public double UiScale => App.Host.Ui.Host is { UseDesignSpace: true } h ? h.EffectiveScale : App.Host.Gpu.DisplayScale;

    /// <summary>Run <paramref name="n"/> frames and wait for the GPU, so the readback holds the last one.</summary>
    public void Frames(int n)
    {
        for (int i = 0; i < n; i++)
        {
            if (!App.RunFrame())
            {
                break;
            }
        }

        App.Host.Gpu.WaitIdle();
    }

    /// <summary>Push a key down or up through SDL's queue (delivered by the next frame's pump).</summary>
    public void Key(SDL_Scancode scancode, bool down)
    {
        SDL_Event e = default;
        e.type = (uint)(down ? SDL_EventType.SDL_EVENT_KEY_DOWN : SDL_EventType.SDL_EVENT_KEY_UP);
        e.key.windowID = (SDL_WindowID)WindowId;
        e.key.scancode = scancode;
        e.key.down = down;
        Push(&e);
    }

    /// <summary>Press and release a key: down this frame, up the next (two frames run).</summary>
    public void Tap(SDL_Scancode scancode)
    {
        Key(scancode, true);
        Frames(1);
        Key(scancode, false);
        Frames(1);
    }

    /// <summary>Move the pointer to a pixel position.</summary>
    public void MoveTo(float px, float py)
    {
        SDL_Event e = default;
        e.type = (uint)SDL_EventType.SDL_EVENT_MOUSE_MOTION;
        e.motion.windowID = (SDL_WindowID)WindowId;
        e.motion.x = px;
        e.motion.y = py;
        Push(&e);
    }

    /// <summary>Click the left button at a pixel position: move, a frame, down, a frame, up, a frame.</summary>
    public void Click(float px, float py)
    {
        MoveTo(px, py);
        Frames(1);
        Button(1, true, px, py);
        Frames(1);
        Button(1, false, px, py);
        Frames(1);
    }

    /// <summary>Click the center of <paramref name="element"/> (laid out in the top window's DIP space).</summary>
    public void Click(UIElement element)
    {
        (float x, float y) = PixelCenter(element);
        Click(x, y);
    }

    /// <summary>The pixel position of <paramref name="element"/>'s center.</summary>
    public (float X, float Y) PixelCenter(UIElement element)
    {
        ArgumentNullException.ThrowIfNull(element);
        Window window = element.FindVisualRoot() as Window ?? throw new InvalidOperationException("the element is not in a window");
        Rect r = element.TranslateRect(new Rect(0, 0, element.Bounds.Width, element.Bounds.Height), window);
        double k = UiScale;
        return ((float)((r.X + (r.Width / 2)) * k), (float)((r.Y + (r.Height / 2)) * k));
    }

    /// <summary>Design-space DIP to pixels (Overlay mode).</summary>
    public (int X, int Y) Design(double x, double y)
    {
        double k = UiScale;
        return ((int)Math.Round(x * k), (int)Math.Round(y * k));
    }

    /// <summary>Turn the wheel by <paramref name="notches"/> (positive = away from the user) at a pixel position; one frame runs.</summary>
    public void Wheel(float notches, float px, float py)
    {
        MoveTo(px, py);
        Frames(1);
        SDL_Event e = default;
        e.type = (uint)SDL_EventType.SDL_EVENT_MOUSE_WHEEL;
        e.wheel.windowID = (SDL_WindowID)WindowId;
        e.wheel.y = notches;
        e.wheel.mouse_x = px;
        e.wheel.mouse_y = py;
        Push(&e);
        Frames(1);
    }

    /// <summary>Drag the left button from one pixel position to another in <paramref name="steps"/> moves (a frame each).</summary>
    public void Drag(float x0, float y0, float x1, float y1, int steps = 4, byte button = 1)
    {
        MoveTo(x0, y0);
        Frames(1);
        Button(button, true, x0, y0);
        Frames(1);
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps;
            MoveTo(x0 + ((x1 - x0) * t), y0 + ((y1 - y0) * t));
            Frames(1);
        }

        Button(button, false, x1, y1);
        Frames(1);
    }

    /// <summary>Resize the SDL window to <paramref name="width"/> x <paramref name="height"/> pixels; the next frame re-fits the swapchain and the UI.</summary>
    public void Resize(int width, int height)
    {
        SDL_SetWindowSize(App.Host.Gpu.Window, width, height);
        Frames(2);
    }

    /// <summary>Type <paramref name="text"/> into the snapshot for the next frame (the SDL text path's data).</summary>
    public void Type(string text)
    {
        _oneFrame.Add(i => i.AppendTextInput(text));
    }

    /// <summary>Hold a gamepad button for the frames that follow until <see cref="ReleasePad"/>.</summary>
    public void HoldPad(GamepadButton button)
    {
        _everyFrame.Add(i =>
        {
            i.GamepadConnected = true;
            i.GamepadKind = DeviceKind.Gamepad;
            i.GamepadButtonDown[(int)button] = true;
        });
    }

    /// <summary>Release every held gamepad button (the pad stays connected).</summary>
    public void ReleasePad()
    {
        _everyFrame.Clear();
        _oneFrame.Add(i =>
        {
            Array.Clear(i.GamepadButtonDown);
            i.GamepadConnected = true;
        });
    }

    /// <summary>Tap a gamepad button: held for one frame, released the next.</summary>
    public void TapPad(GamepadButton button)
    {
        HoldPad(button);
        Frames(1);
        ReleasePad();
        Frames(1);
    }

    public void Dispose()
    {
        App.Host.Gpu.WaitIdle();
        Readback.Dispose();
        App.Dispose();
    }

    private void Button(byte button, bool down, float px, float py)
    {
        SDL_Event e = default;
        e.type = (uint)(down ? SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN : SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP);
        e.button.windowID = (SDL_WindowID)WindowId;
        e.button.button = button;
        e.button.down = down;
        e.button.x = px;
        e.button.y = py;
        Push(&e);
    }

    private static void Push(SDL_Event* e)
    {
        if (!SDL_PushEvent(e))
        {
            throw new InvalidOperationException("SDL_PushEvent failed: " + SDL_GetError());
        }
    }
}
