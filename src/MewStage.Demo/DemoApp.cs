// The embedded demo's application object: it owns the AppHost (SDL window, GPU device, scheduler, render graph, MewUI
// layer), the gamepad capture and the current mode, and switches modes between frames. Overlay and Workbench run
// here; Kit mode runs on upstream's own platform host in a fresh process (see StartPage).
using System;
using System.Diagnostics;

using Aprillz.MewUI;

using MewStage.Host;
using MewStage.Input;
using MewStage.Rendering.Graph;

namespace MewStage.Demo;

/// <summary>The embedded demo (see the file header).</summary>
internal sealed class DemoApp : IDisposable
{
    /// <summary>The default window size, pixels.</summary>
    public const int DefaultWidth = 1280;

    /// <summary>See <see cref="DefaultWidth"/>.</summary>
    public const int DefaultHeight = 720;

    private readonly Stopwatch _frameClock = new();
    private IDemoMode? _pending;
    private bool _disposed;

    /// <param name="options">The parsed command line.</param>
    /// <param name="offscreen">True: an offscreen SDL window and swapchain (the journey harness).</param>
    /// <param name="width">With <paramref name="height"/>: the window size, pixels.</param>
    /// <param name="height">See <paramref name="width"/>.</param>
    public DemoApp(DemoOptions options, bool offscreen = false, int width = DefaultWidth, int height = DefaultHeight)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;

        // The demo is dark: set before the first host so every element built afterwards is styled Dark (the embedded
        // host only resolves a variant nobody named; Kit mode runs upstream's Application and picks its own).
        ThemeManager.Default = ThemeVariant.Dark;

        Host = new AppHost(options.Backend, "MewStage", width, height, DemoFonts.Register, 0, 0, SdlGamepadCapture.InitFlags, offscreen, options.Warp);
        Gamepads = new SdlGamepadCapture();
        Host.EventHook = (e, input) => Gamepads.TryHandleEvent(e, input);
        Host.AfterPump = OnAfterPump;
        Host.BeforeUi = OnBeforeUi;
        Host.Ui.Start();                           // every mode shows MewUI; a chart built before it is shown needs the factory
        Host.Ui.Bridge.FocusFollowsMouse = true;   // the game-style rule: hovering focuses
        Host.Ui.Bridge.TabNavigation = true;       // and Tab / Shift+Tab walk the focus for keyboard users
        _pending = options.Mode switch
        {
            DemoMode.Overlay => new Overlay.OverlayMode(),
            DemoMode.Workbench => new Workbench.WorkbenchMode(),
            DemoMode.Kit => throw new NotSupportedException("Kit mode runs on its own host, not in DemoApp"),
            _ => new StartPage(),
        };
    }

    /// <summary>The parsed command line.</summary>
    public DemoOptions Options { get; }

    /// <summary>The window, GPU and UI layer.</summary>
    public AppHost Host { get; }

    /// <summary>Connected gamepads, mapped into the input snapshot.</summary>
    public SdlGamepadCapture Gamepads { get; }

    /// <summary>The mode on screen (null before the first frame).</summary>
    public IDemoMode? Mode { get; private set; }

    /// <summary>The process exit code <see cref="Run"/> ends with (0 unless a mode asked for a relaunch).</summary>
    public int ExitCode { get; private set; }

    /// <summary>The last frame's CPU time, milliseconds (pump to present), for the HUD readout.</summary>
    public double FrameMilliseconds { get; private set; }

    /// <summary>Switch to <paramref name="mode"/> at the start of the next frame (the current mode leaves first).</summary>
    public void Switch(IDemoMode mode)
    {
        ArgumentNullException.ThrowIfNull(mode);
        _pending = mode;
    }

    /// <summary>Close the window after this frame.</summary>
    public void Quit(int exitCode = 0)
    {
        ExitCode = exitCode;
        Host.Input.CloseRequested = true;
    }

    /// <summary>Run frames until the window closes.</summary>
    public void Run()
    {
        while (RunFrame())
        {
        }
    }

    /// <summary>One frame (the journey harness steps these). False once the window was asked to close.</summary>
    public bool RunFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frameClock.Restart();
        bool more = Host.RunFrame();
        FrameMilliseconds = _frameClock.Elapsed.TotalMilliseconds;
        return more;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Host.Gpu.WaitIdle();
        Mode?.Leave();
        Mode = null;
        Gamepads.Dispose();
        Host.Dispose();
    }

    private void OnAfterPump(RawInputSnapshot input)
    {
        if (_pending is { } next)
        {
            _pending = null;
            if (Mode is { } current)
            {
                Host.Gpu.WaitIdle();   // the leaving mode frees GPU objects the last frame may still read
                current.Leave();
            }

            Mode = next;
            next.Enter(this);
        }

        Mode?.Tick(Host.DeltaSeconds);
    }

    private void OnBeforeUi(RenderGraph graph, RgHandle backbuffer)
    {
        Mode?.Render(graph, backbuffer);
    }
}
