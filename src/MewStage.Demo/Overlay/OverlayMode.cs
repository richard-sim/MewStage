// Overlay mode: the cube scene drawn straight into the backbuffer by CubeSceneRenderer (clear + depth + grid + cube),
// then the `ui` pass LOADS the backbuffer and composites the window stack over it: the HUD (always), the pause menu
// (Escape / Start / B) and the quit dialog, bottom to top. Only the top window takes input. The UI is laid out in a
// 1920 x 1080 design space scaled to fit the window; the mode sets that space on entry and clears it on leave.
// Windows the mode drops are closed (Window.Close), which returns their cached icons to the UI's scratch pool. A
// window dropped by its own button (Resume, Cancel) is closed at the next Tick, outside that button's handler:
// the embedded host closes synchronously and would dispose the tree under the handler that is still running.
using System;
using System.Collections.Generic;

using Aprillz.MewUI;

using MewStage.Graphics;
using MewStage.Host;
using MewStage.Rendering.Camera;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;
using MewStage.Scene;
using MewStage.Ui.Navigation;

namespace MewStage.Demo.Overlay;

/// <summary>UI over a world in one backbuffer (see the file header).</summary>
internal sealed class OverlayMode : IDemoMode, IDisposable
{
    /// <summary>Seconds the HUD hint stays before it fades.</summary>
    public const double HintSeconds = 3.0;

    /// <summary>The camera's idle orbit, radians per second.</summary>
    public const double OrbitSpeed = 0.1;

    private static readonly ClearColor WorldBackground = new(0.075f, 0.085f, 0.11f, 1f);

    private readonly Window[] _one = new Window[1];
    private readonly Window[] _two = new Window[2];
    private readonly Window[] _three = new Window[3];
    private readonly List<Window> _toClose = [];   // dropped this frame, closed at the next Tick (reused, no per-frame alloc)
    private DemoApp? _app;
    private ClipConvention _clip;
    private bool _dialogNeedsFadeIn;
    private double _hintAge;
    private bool _hintFaded;

    /// <summary>The shared observable state.</summary>
    public HudViewModel ViewModel { get; } = new();

    /// <summary>The orbit camera (idle orbit around the cube).</summary>
    public OrbitCamera Camera { get; } = new();

    /// <summary>The scene state (settings and spin), shared with the scene renderer.</summary>
    public CubeScene World { get; } = new();

    /// <summary>The scene renderer (null until entered).</summary>
    public CubeSceneRenderer? Scene { get; private set; }

    /// <summary>The HUD (null until entered).</summary>
    public HudView? Hud { get; private set; }

    /// <summary>The pause menu while open.</summary>
    public PauseMenu? Pause { get; private set; }

    /// <summary>The quit dialog while open.</summary>
    public QuitDialog? Dialog { get; private set; }

    /// <summary>True while the pause menu (or the dialog above it) is open: the cube stops spinning.</summary>
    public bool IsPaused => Pause is not null;

    /// <inheritdoc />
    public void Enter(DemoApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
        AppHost host = app.Host;
        GpuBackend backend = host.Gpu.Backend;
        _clip = backend == GpuBackend.Vulkan ? ClipConvention.Vulkan : ClipConvention.D3DMetal;
        var shaders = SceneShaders.Load(backend);
        Scene = new CubeSceneRenderer(host.Gpu.Device, shaders.CubeVs, shaders.CubePs, shaders.GridVs, shaders.GridPs, host.Gpu.Swapchain.Format, World);
        Camera.Target = new System.Numerics.Vector3(0f, 0f, 0.6f);
        Camera.Distance = 4.5f;
        Camera.Pitch = 0.42f;

        host.UiClearsBackground = false;
        host.Background = WorldBackground;
        host.Ui.SetDesignSpace(HudView.DesignWidth, HudView.DesignHeight);

        Hud = HudView.Build(ViewModel);
        Hud.Window.Commands.Register(NavCommands.Confirm, OpenPause);
        Hud.Window.Commands.Register(NavCommands.Back, OpenPause);
        NavCommands.MapKeys(Hud.Window);
        _hintAge = 0;
        _hintFaded = false;
        PushStack();
    }

    /// <inheritdoc />
    public void Tick(double dt)
    {
        if (_app is null || Scene is null || Hud is null)
        {
            return;
        }

        CloseDropped();

        // Settings from the menu into the scene.
        SceneSettings s = World.Settings;
        s.SpinSpeed = (float)ViewModel.SpinSpeed.Value;
        s.ShowGrid = ViewModel.ShowGrid.Value;
        s.Tint = ViewModel.Tint;

        if (!IsPaused)
        {
            World.Advance(dt);
        }

        Camera.Yaw += (float)(OrbitSpeed * dt);
        ViewModel.SetReadouts(_app.FrameMilliseconds, World.Angle);

        if (!_hintFaded && !IsPaused)
        {
            _hintAge += dt;
            if (_hintAge >= HintSeconds)
            {
                _hintFaded = true;
                Hud.FadeOutHint();
            }
        }

        if (_dialogNeedsFadeIn && Dialog is { } dialog)
        {
            // One frame after Show: the window has laid out at Opacity 0, the Transition now animates to 1.
            _dialogNeedsFadeIn = false;
            dialog.FadeIn();
        }
    }

    /// <inheritdoc />
    public void Render(RenderGraph graph, RgHandle backbuffer)
    {
        if (_app is null || Scene is null)
        {
            return;
        }

        FrameContext frame = _app.Host.CurrentFrame ?? throw new InvalidOperationException("Render outside a frame");
        var cam = Camera.ToRenderCamera(_clip);
        Scene.AddPasses(graph, backbuffer, (uint)_app.Host.Gpu.PixelWidth, (uint)_app.Host.Gpu.PixelHeight, in cam, frame, clear: true, WorldBackground);
    }

    /// <inheritdoc />
    public void Leave()
    {
        if (_app is { } app)
        {
            app.Host.Ui.SetWindow(null);
            app.Host.UiClearsBackground = true;
            app.Host.Ui.SetDesignSpace(0, 0);
        }

        // Close every window the mode still holds (already out of the stack, so nothing re-renders): each tree is
        // disposed and its icon caches go back to the pool.
        CloseDropped();
        Dialog?.Window.Close();
        Pause?.Window.Close();
        Hud?.Window.Close();

        Scene?.Dispose();
        Scene = null;
        Hud = null;
        Pause = null;
        Dialog = null;
        _app = null;
    }

    /// <summary>Same as <see cref="Leave"/> (the scene renderer is the only GPU object the mode owns).</summary>
    public void Dispose()
    {
        Leave();
    }

    /// <summary>Open the pause menu (Escape / Start / B on the HUD).</summary>
    public void OpenPause()
    {
        if (Pause is not null || _app is null)
        {
            return;
        }

        Pause = PauseMenu.Build(ViewModel, ClosePause, ToStartPage, OpenQuitDialog);
        PushStack();
    }

    /// <summary>Close the pause menu (Resume / Escape / B).</summary>
    public void ClosePause()
    {
        if (Pause is null)
        {
            return;
        }

        if (Dialog is { } dialog)
        {
            _toClose.Add(dialog.Window);
        }

        _toClose.Add(Pause.Window);
        Dialog = null;
        Pause = null;
        Hud?.ShowHint();
        _hintAge = 0;
        _hintFaded = false;
        PushStack();
    }

    /// <summary>Open the quit confirmation above the pause menu.</summary>
    public void OpenQuitDialog()
    {
        if (Dialog is not null || Pause is null)
        {
            return;
        }

        Dialog = QuitDialog.Build(CloseQuitDialog, ConfirmQuit);
        _dialogNeedsFadeIn = true;
        PushStack();
    }

    /// <summary>Close the quit confirmation (Cancel / Escape / B).</summary>
    public void CloseQuitDialog()
    {
        if (Dialog is null)
        {
            return;
        }

        _toClose.Add(Dialog.Window);
        Dialog = null;
        PushStack();
    }

    private void ConfirmQuit()
    {
        _app?.Quit();
    }

    private void ToStartPage()
    {
        _app?.Switch(new StartPage());
    }

    /// <summary>Close the windows dropped since the last Tick (they already left the stack).</summary>
    private void CloseDropped()
    {
        for (int i = 0; i < _toClose.Count; i++)
        {
            _toClose[i].Close();
        }

        _toClose.Clear();
    }

    private void PushStack()
    {
        if (_app is null || Hud is null)
        {
            return;
        }

        if (Dialog is { } d && Pause is { } p)
        {
            _three[0] = Hud.Window;
            _three[1] = p.Window;
            _three[2] = d.Window;
            _app.Host.Ui.SetWindows(_three);
        }
        else if (Pause is { } pause)
        {
            _two[0] = Hud.Window;
            _two[1] = pause.Window;
            _app.Host.Ui.SetWindows(_two);
        }
        else
        {
            _one[0] = Hud.Window;
            _app.Host.Ui.SetWindows(_one);
        }
    }
}
