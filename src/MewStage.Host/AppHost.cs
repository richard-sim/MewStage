// The application's frame loop on the GPU + UI stack: a GpuWindow + UiLayer, an SDL event pump (quit, keyboard, typed
// text, mouse, wheel incl. horizontal, pen, resize; anything else to an optional hook, e.g. the gamepad capture), and
// per frame: input to the top window, resize, the application's own passes (BeforeUi: a world, offscreen viewports),
// the `ui` pass (cleared to the page color when nothing else drew), the application's terminal passes (AfterUi), an
// optional readback (BeforePresent), present. Secondary OS windows: the pump routes each event to the window it
// names; every frame runs each open secondary window's input step after the main one and renders it after the main
// graph was submitted, inside the same scheduler frame (one frame loop for every window; see SecondaryWindow).
using System;
using System.Collections.Generic;
using System.Diagnostics;

using MewStage.Graphics;
using MewStage.Input;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;
using MewStage.Ui.Backend;

using SDL;

using static SDL.SDL3;

namespace MewStage.Host;

/// <summary>An application window running MewUI on the GPU stack (see the file header).</summary>
public sealed unsafe class AppHost : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Action<PassBuilder> _clearSetup;
    private readonly RgExecute _clearExec;
    private readonly List<SecondaryWindow> _secondaries = [];
    private double _lastSeconds;
    private RgHandle _bb;
    private bool _disposed;

    /// <param name="backend">The GPU API (see <see cref="GpuBackends.Select"/>).</param>
    /// <param name="title">The window title.</param>
    /// <param name="width">With <paramref name="height"/>: the initial window size, pixels.</param>
    /// <param name="height">See <paramref name="width"/>.</param>
    /// <param name="registerFonts">Adds the application's faces to the UI backend.</param>
    /// <param name="designWidth">With <paramref name="designHeight"/>: the UI's design space (0 = 1:1 DIP).</param>
    /// <param name="designHeight">See <paramref name="designWidth"/>.</param>
    /// <param name="extraInitFlags">Additional SDL subsystems (e.g. gamepad and joystick for a pad capture hook).</param>
    /// <param name="offscreen">True: the window is offscreen (<see cref="GpuWindow.Create"/> offscreen; the journey
    /// harness on lavapipe). The frame loop, the SDL pump and the UI layer are unchanged.</param>
    /// <param name="useWarp">DX12 only: the WARP software adapter.</param>
    public AppHost(GpuBackend backend, string title, int width, int height, Action<FontCollection>? registerFonts, double designWidth, double designHeight, SDL_InitFlags extraInitFlags = 0, bool offscreen = false, bool useWarp = false)
    {
        Gpu = new GpuWindow(backend, useWarp);
        Gpu.Create(title, width, height, extraInitFlags, offscreen);
        Ui = new UiLayer(Gpu, registerFonts, designWidth, designHeight);
        _clearSetup = ClearSetup;
        _clearExec = ClearExecute;
    }

    /// <summary>The window, device, swapchain, scheduler and render graph.</summary>
    public GpuWindow Gpu { get; }

    /// <summary>The MewUI layer (window stack, input bridge, navigator).</summary>
    public UiLayer Ui { get; }

    /// <summary>This frame's input (filled by the pump, consumed by the UI, deltas cleared at the frame's end).</summary>
    public RawInputSnapshot Input { get; } = new();

    /// <summary>
    /// Called for every SDL event the pump does not handle itself; true = consumed. A gamepad capture
    /// (<see cref="SdlGamepadCapture.TryHandleEvent"/>) plugs in here.
    /// </summary>
    public Func<SDL_Event, RawInputSnapshot, bool>? EventHook { get; set; }

    /// <summary>Called after the pump, before the UI gets the frame's input.</summary>
    public Action<RawInputSnapshot>? AfterPump { get; set; }

    /// <summary>
    /// Called every frame after the backbuffer import and BEFORE the `ui` pass: the world passes that draw under the UI,
    /// and offscreen passes the UI samples (viewports), declared to the UI host with
    /// <see cref="EmbeddedUiHost.AddSampledTexture"/>, are added here. Set <see cref="UiClearsBackground"/> false when
    /// these passes fill the backbuffer.
    /// </summary>
    public Action<RenderGraph, RgHandle>? BeforeUi { get; set; }

    /// <summary>
    /// Called every frame AFTER the `ui` pass and before present: terminal passes that return persistent offscreen
    /// targets to the state their next-frame import declares.
    /// </summary>
    public Action<RenderGraph>? AfterUi { get; set; }

    /// <summary>
    /// Called every frame after <see cref="AfterUi"/> and before the present pass, with the backbuffer's graph handle
    /// and texture: a readback of the composed window (the journey harness). Null = none.
    /// </summary>
    public Action<RenderGraph, RgHandle, ITexture>? BeforePresent { get; set; }

    /// <summary>
    /// True (the default): the `ui` pass CLEARS the backbuffer to <see cref="Background"/> before drawing (nothing drew
    /// under the UI). False: it LOADS what <see cref="BeforeUi"/> drew and composites the UI over it (Overlay mode).
    /// </summary>
    public bool UiClearsBackground { get; set; } = true;

    /// <summary>The color the backbuffer is cleared to when the UI did not draw (no window shown), and the `ui` pass's clear color.</summary>
    public ClearColor Background
    {
        get;
        set
        {
            field = value;
            if (Ui.Host is { } host)
            {
                host.MenuBackground = value;
            }
        }
    } = new(0f, 0f, 0f, 1f);

    /// <summary>
    /// The frame being recorded (upload ring, compute recorder) between BeginFrame and Submit; null outside it.
    /// <see cref="BeforeUi"/> / <see cref="AfterUi"/> callbacks that need GPU work before the passes run read it.
    /// </summary>
    public FrameContext? CurrentFrame { get; private set; }

    /// <summary>Seconds since the host started (the frame clock the loop times itself with).</summary>
    public double Seconds => _clock.Elapsed.TotalSeconds;

    /// <summary>Frames run so far.</summary>
    public long FrameCount { get; private set; }

    /// <summary>Seconds the current (or last) frame advanced the clock by: what <see cref="AfterPump"/> and the UI get as dt.</summary>
    public double DeltaSeconds { get; private set; }

    /// <summary>The open secondary OS windows, in opening order.</summary>
    public IReadOnlyList<SecondaryWindow> Secondaries => _secondaries;

    /// <summary>OS windows of this host: the main window and the open secondary windows.</summary>
    public int WindowCount => 1 + _secondaries.Count;

    /// <summary>Run frames until the window is closed.</summary>
    public void Run()
    {
        while (RunFrame())
        {
        }
    }

    /// <summary>
    /// Opens a secondary OS window (see <see cref="SecondaryWindow"/>) titled <paramref name="title"/> with its top-left
    /// corner at (<paramref name="x"/>, <paramref name="y"/>), desktop logical units, owned by the main window. It joins
    /// the frame from the next <see cref="RunFrame"/>. Throws when SDL or the swapchain cannot be created.
    /// </summary>
    public SecondaryWindow OpenSecondary(string title, int x, int y, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var w = new SecondaryWindow(this, title, x, y, width, height);
        _secondaries.Add(w);
        return w;
    }

    /// <summary>
    /// Closes and frees <paramref name="window"/> (drains the queue first). Call between frames; no-op for a window this
    /// host does not hold.
    /// </summary>
    public void CloseSecondary(SecondaryWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!_secondaries.Remove(window))
        {
            return;
        }

        window.Dispose();
    }

    /// <summary>One frame: pump, input, resize, draw, present. False once the window was asked to close.</summary>
    public bool RunFrame()
    {
        Pump();
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastSeconds;
        _lastSeconds = now;
        DeltaSeconds = dt;
        AfterPump?.Invoke(Input);
        Gpu.RefreshMetrics();
        Ui.Dispatch(Input, dt);
        if (Gpu.EnsureSize())
        {
            Ui.Resize();
        }

        for (int i = 0; i < _secondaries.Count; i++)
        {
            _secondaries[i].BeginFrame(dt);
        }

        FrameContext ctx = Gpu.BeginFrame(out ITexture backbuffer);
        CurrentFrame = ctx;
        _bb = Gpu.ImportBackbuffer(backbuffer);
        BeforeUi?.Invoke(Gpu.Graph, _bb);
        if (Ui.Host is { } host)
        {
            host.MenuBackground = Background;
        }

        if (!Ui.Render(Gpu.Graph, _bb, UiClearsBackground) && UiClearsBackground)
        {
            Gpu.Graph.AddPass("ui-fallback-clear", PassKind.Graphics, _clearSetup);
        }

        AfterUi?.Invoke(Gpu.Graph);
        BeforePresent?.Invoke(Gpu.Graph, _bb, backbuffer);
        Gpu.Submit(ctx, _bb);
        for (int i = 0; i < _secondaries.Count; i++)
        {
            _secondaries[i].Render(ctx);
        }

        Gpu.EndFrame(ctx);
        CurrentFrame = null;
        Input.ClearDeltas();
        FrameCount++;
        return !Input.CloseRequested;
    }

    /// <summary>
    /// Translate one SDL event into <paramref name="input"/> for <paramref name="window"/> (the main window's or a
    /// secondary window's snapshot). True = the window was asked to close. Public so an application with its own pump
    /// translates events exactly as the host does.
    /// </summary>
    public static bool ApplyEvent(in SDL_Event e, RawInputSnapshot input, SDL_Window* window)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (SdlPenAndWheel.Apply(e, input))
        {
            return false;   // pen events; the wheel's X (its Y is added below)
        }

        switch ((SDL_EventType)e.type)
        {
            case SDL_EventType.SDL_EVENT_QUIT:
            case SDL_EventType.SDL_EVENT_WINDOW_CLOSE_REQUESTED:
                return true;
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_LOST:
                // Keys and buttons released while another window has the keyboard never reach this one: release them
                // here, or they stay held in this window's snapshot (the bridge then sends the key-ups).
                Array.Clear(input.KeyDown);
                Array.Clear(input.MouseButtonDown);
                input.Focused = false;
                return false;
            case SDL_EventType.SDL_EVENT_WINDOW_FOCUS_GAINED:
                input.Focused = true;
                return false;
            case SDL_EventType.SDL_EVENT_KEY_DOWN:
            case SDL_EventType.SDL_EVENT_KEY_UP:
                ApplyKey(e.key.scancode, e.key.down, input);
                return false;
            case SDL_EventType.SDL_EVENT_TEXT_INPUT:
                SdlKeys.AppendText(e.text.text, input);
                return false;
            case SDL_EventType.SDL_EVENT_MOUSE_MOTION:
                input.MouseX = e.motion.x;
                input.MouseY = e.motion.y;
                input.MouseDeltaX += e.motion.xrel;
                input.MouseDeltaY += e.motion.yrel;
                return false;
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN:
            case SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP:
                ApplyMouseButton(e.button.button, e.button.down, input);
                return false;
            case SDL_EventType.SDL_EVENT_MOUSE_WHEEL:
                input.MouseWheel += e.wheel.y;
                return false;
            case SDL_EventType.SDL_EVENT_WINDOW_RESIZED:
            case SDL_EventType.SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
            case SDL_EventType.SDL_EVENT_WINDOW_DISPLAY_CHANGED:
                ApplyWindowSize(window, input);
                return false;
            default:
                return false;
        }
    }

    /// <summary>The <see cref="MouseButton"/> index for an SDL mouse button number, or -1 (SDL: 1 left, 2 middle, 3 right).</summary>
    public static int MapMouseButton(byte sdlButton)
    {
        return sdlButton switch
        {
            1 => (int)MouseButton.Left,
            3 => (int)MouseButton.Right,
            2 => (int)MouseButton.Middle,
            _ => -1,
        };
    }

    /// <summary>Apply a key edge for <paramref name="scancode"/> to the snapshot (unmapped scancodes are ignored).</summary>
    public static void ApplyKey(SDL_Scancode scancode, bool down, RawInputSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(input);
        KeyCode k = SdlKeys.MapScancode(scancode);
        if (k != KeyCode.None)
        {
            input.KeyDown[(int)k] = down;
        }
    }

    /// <summary>Apply a mouse-button edge to the snapshot (buttons beyond the three are ignored).</summary>
    public static void ApplyMouseButton(byte sdlButton, bool down, RawInputSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(input);
        int b = MapMouseButton(sdlButton);
        if (b >= 0)
        {
            input.MouseButtonDown[b] = down;
        }
    }

    private static void ApplyWindowSize(SDL_Window* window, RawInputSnapshot input)
    {
        if (window == null)
        {
            return;
        }

        int w;
        int h;
        SDL_GetWindowSizeInPixels(window, &w, &h);
        if (w > 0 && h > 0)
        {
            input.WindowWidth = w;
            input.WindowHeight = h;
        }
    }

    private void Pump()
    {
        SDL_Event e;
        while (SDL_PollEvent(&e))
        {
            // An event that names a secondary window is that window's (its input, its close button).
            if (SecondaryFor(EventWindowId(e)) is { } secondary)
            {
                if ((SDL_EventType)e.type == SDL_EventType.SDL_EVENT_WINDOW_MOVED)
                {
                    secondary.OnMoved(e.window.data1, e.window.data2);
                }
                else if (ApplyEvent(e, secondary.Input, secondary.Window))
                {
                    secondary.OnCloseRequested();
                }

                continue;
            }

            if (EventHook is not null && EventHook(e, Input))
            {
                continue;
            }

            if (ApplyEvent(e, Input, Gpu.Window))
            {
                Input.CloseRequested = true;
            }
        }
    }

    private SecondaryWindow? SecondaryFor(uint windowId)
    {
        if (windowId == 0)
        {
            return null;
        }

        for (int i = 0; i < _secondaries.Count; i++)
        {
            if (_secondaries[i].WindowId == windowId)
            {
                return _secondaries[i];
            }
        }

        return null;
    }

    // The SDL window an event names (0 = none: quit, gamepad, device events go to the main window).
    private static uint EventWindowId(in SDL_Event e)
    {
        var t = (SDL_EventType)e.type;
        return t switch
        {
            SDL_EventType.SDL_EVENT_KEY_DOWN or SDL_EventType.SDL_EVENT_KEY_UP => (uint)e.key.windowID,
            SDL_EventType.SDL_EVENT_TEXT_INPUT => (uint)e.text.windowID,
            SDL_EventType.SDL_EVENT_TEXT_EDITING => (uint)e.edit.windowID,
            SDL_EventType.SDL_EVENT_MOUSE_MOTION => (uint)e.motion.windowID,
            SDL_EventType.SDL_EVENT_MOUSE_BUTTON_DOWN or SDL_EventType.SDL_EVENT_MOUSE_BUTTON_UP => (uint)e.button.windowID,
            SDL_EventType.SDL_EVENT_MOUSE_WHEEL => (uint)e.wheel.windowID,
            SDL_EventType.SDL_EVENT_PEN_PROXIMITY_IN or SDL_EventType.SDL_EVENT_PEN_PROXIMITY_OUT => (uint)e.pproximity.windowID,
            SDL_EventType.SDL_EVENT_PEN_DOWN or SDL_EventType.SDL_EVENT_PEN_UP => (uint)e.ptouch.windowID,
            SDL_EventType.SDL_EVENT_PEN_BUTTON_DOWN or SDL_EventType.SDL_EVENT_PEN_BUTTON_UP => (uint)e.pbutton.windowID,
            SDL_EventType.SDL_EVENT_PEN_MOTION => (uint)e.pmotion.windowID,
            SDL_EventType.SDL_EVENT_PEN_AXIS => (uint)e.paxis.windowID,
            _ => t is >= SDL_EventType.SDL_EVENT_WINDOW_FIRST and <= SDL_EventType.SDL_EVENT_WINDOW_LAST ? (uint)e.window.windowID : 0,
        };
    }

    private void ClearSetup(PassBuilder s)
    {
        s.Write(_bb, ResourceState.RenderTarget);
        s.SetExecute(_clearExec);
    }

    private void ClearExecute(in RgPassContext ctx)
    {
        ulong color = ctx.Resolve(_bb);
        IGraphicsRecorder rec = ctx.Recorder;
        Span<ColorAttachment> ca = [new ColorAttachment { TextureHandle = color, Clear = true, ClearValue = Background }];
        rec.BeginRendering(ca, default, (uint)Gpu.PixelWidth, (uint)Gpu.PixelHeight);
        rec.EndRendering();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Gpu.WaitIdle();   // the UI's GPU resources are freed against an idle device

        // Secondary windows first: their swapchains live on this device, and SDL destroys an owned window with its
        // owner (SDL_DestroyWindow), which would leave their window pointers dangling.
        for (int i = _secondaries.Count - 1; i >= 0; i--)
        {
            _secondaries[i].Dispose();
        }

        _secondaries.Clear();
        Ui.Dispose();
        Gpu.Dispose();
    }
}
