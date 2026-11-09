// Workbench mode: a 3D viewport INSIDE the UI. One MewUI window in DIP space (a tool, not a game: hover does not
// focus, Tab walks the focus) with a menu bar, a toolbar, a status bar and a MewDock DockingManager holding the
// Viewport (and an optional Viewport 2), the Outliner, the Inspector and the Stats chart. Each viewport is a
// ViewportControl the ViewportCompositor renders offscreen before the `ui` pass (into a target sized to the control,
// imported into the UI as a zero-copy image) and returns to Common after it. The dock layout is saved to the user
// settings folder on leave and restored on enter; the Stats panel can detach into a secondary OS window. Leaving
// closes the window (Window.Close), which disposes its tree and returns the icon caches to the scratch pool.
using System;
using System.Globalization;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.MewDock;

using MewStage.Graphics;
using MewStage.Host;
using MewStage.Rendering.Camera;
using MewStage.Rendering.Graph;
using MewStage.Scene;
using MewStage.Ui.Backend;
using MewStage.Viewport;

namespace MewStage.Demo.Workbench;

/// <summary>A 3D viewport inside a docked tool UI (see the file header).</summary>
internal sealed class WorkbenchMode : IDemoMode, IDisposable
{
    /// <summary>The settings file the dock layout is saved to.</summary>
    public const string LayoutFile = "workbench-layout.json";

    /// <summary>File > Quit.</summary>
    public static readonly Command QuitCommand = new("demo.workbench.quit", "_Quit", Icon("close"));

    /// <summary>File > Start page.</summary>
    public static readonly Command StartPageCommand = new("demo.workbench.start-page", "_Start page", Icon("open"));

    /// <summary>View > Viewport 2 (toggle).</summary>
    public static readonly Command ToggleViewport2Command = new("demo.workbench.viewport2", "Viewport _2", Icon("camera"));

    /// <summary>View > Stats Window (toggle: detach / dock the stats chart).</summary>
    public static readonly Command StatsWindowCommand = new("demo.workbench.stats-window", "Stats _Window", Icon("new"));

    /// <summary>View > Reset Layout.</summary>
    public static readonly Command ResetLayoutCommand = new("demo.workbench.reset-layout", "_Reset Layout", Icon("settings"));

    /// <summary>Help > About.</summary>
    public static readonly Command AboutCommand = new("demo.workbench.about", "_About MewStage", Icon("play"));

    private static readonly ClearColor WindowBackground = new(0.09f, 0.10f, 0.12f, 1f);

    private DemoApp? _app;
    private ClipConvention _clip;
    private CubeViewportRenderer? _rendererA;
    private CubeViewportRenderer? _rendererB;
    private OrbitInputSink? _sinkA;
    private OrbitInputSink? _sinkB;
    private DockPane? _viewportPane;
    private DockPane? _viewport2Pane;
    private DockPane? _statsPane;
    private Grid? _about;
    private double _lastFrameMs;

    /// <summary>The observable state.</summary>
    public WorkbenchViewModel ViewModel { get; } = new();

    /// <summary>The shared scene (settings and spin).</summary>
    public CubeScene World { get; } = new();

    /// <summary>Camera A (the first viewport).</summary>
    public OrbitCamera CameraA { get; } = new() { Yaw = -0.6f, Pitch = 0.45f, Distance = 4.5f };

    /// <summary>Camera B (the second viewport: a top-down view).</summary>
    public OrbitCamera CameraB { get; } = new() { Yaw = 0.8f, Pitch = 1.2f, Distance = 6f };

    /// <summary>The window (null until entered).</summary>
    public Window? Window { get; private set; }

    /// <summary>The dock (null until entered).</summary>
    public DockingManager? Dock { get; private set; }

    /// <summary>The first viewport.</summary>
    public ViewportControl ViewportA { get; } = new();

    /// <summary>The second viewport.</summary>
    public ViewportControl ViewportB { get; } = new();

    /// <summary>The compositor (null until entered).</summary>
    public ViewportCompositor? Compositor { get; private set; }

    /// <summary>The outliner list (left pane), bound to the view model's selection.</summary>
    public ListBox? OutlinerList { get; private set; }

    /// <summary>The property grid (right pane) over the selected object.</summary>
    public Inspector? InspectorPanel { get; private set; }

    /// <summary>The stats panel.</summary>
    public StatsPanel? Stats { get; private set; }

    /// <summary>The stats window while detached.</summary>
    public SecondaryWindow? StatsWindow { get; private set; }

    /// <summary>True while the About dialog is open in the window's overlay layer.</summary>
    public bool IsAboutOpen => _about is not null;

    /// <summary>The default dock layout (FlexLayout JSON).</summary>
    public static string DefaultLayout => DockLayoutJson.Root(
        DockLayoutJson.Tools(18, ("Outliner", "outliner")),
        DockLayoutJson.Row(57, DockLayoutJson.Document(72, ("Viewport", "viewport")), DockLayoutJson.Tools(28, ("Stats", "stats"))),
        DockLayoutJson.Tools(25, ("Inspector", "inspector")));

    /// <inheritdoc />
    public void Enter(DemoApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
        AppHost host = app.Host;
        GpuBackend backend = host.Gpu.Backend;
        _clip = backend == GpuBackend.Vulkan ? ClipConvention.Vulkan : ClipConvention.D3DMetal;
        var shaders = SceneShaders.Load(backend);
        _rendererA = new CubeViewportRenderer(host.Gpu.Device, shaders, World);
        _rendererB = new CubeViewportRenderer(host.Gpu.Device, shaders, World) { Background = new ClearColor(0.13f, 0.12f, 0.16f, 1f) };
        _sinkA = new OrbitInputSink(CameraA, World, _clip);
        _sinkB = new OrbitInputSink(CameraB, World, _clip);
        ViewportA.InputSink = _sinkA;
        ViewportB.InputSink = _sinkB;
        ViewportA.Labels.Clear();
        ViewportA.Labels.Add(new ViewportLabel(World.Center + new System.Numerics.Vector3(0, 0, World.Radius + 0.15f), "Cube", Color.FromRgb(230, 232, 236)));
        ViewportA.InvalidateLabels();

        host.UiClearsBackground = true;
        host.Background = WindowBackground;
        host.Ui.SetDesignSpace(0, 0);
        host.Ui.Bridge.FocusFollowsMouse = false;
        host.Ui.Bridge.TabNavigation = true;

        Window = BuildWindow(app);
        host.Ui.SetWindow(Window);
        Compositor = new ViewportCompositor(host.Gpu.Device, host.Ui.Host ?? throw new InvalidOperationException("the UI host did not start"), tightReadbackRows: backend != GpuBackend.Dx12, host.Gpu.Scheduler.FramesInFlight);
        Compositor.Attach(ViewportA, _rendererA);
        Compositor.Attach(ViewportB, _rendererB);
        host.AfterUi = OnAfterUi;
    }

    /// <inheritdoc />
    public void Tick(double dt)
    {
        if (_app is null || _sinkA is null || _sinkB is null)
        {
            return;
        }

        ViewModel.Apply(World);
        World.Advance(dt);
        ViewportA.Camera = _sinkA.Render();
        ViewportB.Camera = _sinkB.Render();
        ViewModel.CameraAInfo.Set(WorkbenchViewModel.DescribeCamera(CameraA));
        ViewModel.CameraBInfo.Set(WorkbenchViewModel.DescribeCamera(CameraB));

        _lastFrameMs = _app.FrameMilliseconds;
        EmbeddedUiHost? ui = _app.Host.Ui.Host;
        Stats?.Push(_lastFrameMs, ui?.LastFrameDrawCalls ?? 0);
        ViewModel.Status.Set(string.Create(CultureInfo.InvariantCulture,
            $"{_app.Options.Backend}   |   {_lastFrameMs:0.0} ms   |   Viewport {ViewportA.TargetPixelWidth} x {ViewportA.TargetPixelHeight}   |   Viewport 2 {(ViewModel.Viewport2Visible.Value ? $"{ViewportB.TargetPixelWidth} x {ViewportB.TargetPixelHeight}" : "off")}   |   UI caches {ui?.LiveScratchSurfaces ?? 0}, rasterized {ui?.TotalScratchRenders ?? 0}x"));

        if (StatsWindow is { CloseRequested: true })
        {
            DockStats();
        }
    }

    /// <inheritdoc />
    public void Render(RenderGraph graph, RgHandle backbuffer)
    {
        if (_app is null || Compositor is null || _rendererA is null || _rendererB is null)
        {
            return;
        }

        _rendererA.Frame = _app.Host.CurrentFrame;
        _rendererB.Frame = _app.Host.CurrentFrame;
        Compositor.AddViewportPasses(graph, (float)_app.Host.Seconds);
    }

    /// <inheritdoc />
    public void Leave()
    {
        if (_app is { } app)
        {
            SaveLayout();
            if (StatsWindow is { } w)
            {
                app.Host.CloseSecondary(w);
                StatsWindow = null;
            }

            app.Host.AfterUi = null;
            app.Host.Ui.SetWindow(null);
            app.Host.Ui.Bridge.FocusFollowsMouse = true;
        }

        // Close the window (out of the stack already; the layout is saved): its tree is disposed and the toolbar and
        // outliner icon caches go back to the UI's scratch pool, where a re-entered Workbench finds them again.
        Window?.Close();
        Compositor?.Dispose();
        Compositor = null;
        ViewportA.Image = null;
        ViewportB.Image = null;
        _rendererA?.Dispose();
        _rendererB?.Dispose();
        _rendererA = null;
        _rendererB = null;
        Window = null;
        Dock = null;
        _about?.Dispose();
        _about = null;
        _app = null;
    }

    /// <summary>Same as <see cref="Leave"/>.</summary>
    public void Dispose()
    {
        Leave();
    }

    /// <summary>Show or hide the second viewport (View > Viewport 2).</summary>
    public void SetViewport2(bool visible)
    {
        if (Dock is null || _viewportPane is null)
        {
            return;
        }

        if (visible && _viewport2Pane is null)
        {
            DockGroup? group = _viewportPane.Group;
            _viewport2Pane = group is not null
                ? group.AddPane("Viewport 2", ViewportB, "viewport2")
                : Dock.AddDocumentPane("Viewport 2", ViewportB, "viewport2");
        }
        else if (!visible && _viewport2Pane is { } pane)
        {
            _viewport2Pane = null;
            pane.Close();
        }

        ViewModel.Viewport2Visible.Set(visible);
    }

    /// <summary>Detach the stats chart into its own OS window (View > Stats Window), or dock it back.</summary>
    public void ToggleStatsWindow()
    {
        if (StatsWindow is null)
        {
            DetachStats();
        }
        else
        {
            DockStats();
        }
    }

    /// <summary>Frame the cube in camera A (the inspector's button).</summary>
    public void FrameCameraA()
    {
        _sinkA?.FrameCube();
    }

    /// <summary>Frame the cube in camera B.</summary>
    public void FrameCameraB()
    {
        _sinkB?.FrameCube();
    }

    /// <summary>Open the About dialog in the window's overlay layer.</summary>
    public void ShowAbout()
    {
        if (Window is null || _about is not null)
        {
            return;
        }

        var about = new Grid();
        about.Add(new Border { Background = Color.FromArgb(140, 0, 0, 0) });
        StackPanel body = new StackPanel().Vertical().Spacing(14);
        body.Add(new TextBlock().Text("MewStage").FontFamily(DemoFonts.Display).FontSize(28).FontWeight(FontWeight.Bold));
        body.Add(new TextBlock().Text("MewUI and MewVG embedded in an application that owns its window, GPU device and render graph. This workbench shows a 3D viewport inside a docked tool UI: the world renders offscreen and the UI samples it as an image.").TextWrapping(TextWrapping.Wrap).Width(420));
        DemoButton close = DemoButton.Create(DemoStyles.Primary, "Close").OnClick(CloseAbout);
        body.Add(close.Right());
        about.Add(new Border
        {
            Background = Color.FromRgb(36, 39, 48),
            BorderBrush = Color.FromRgb(90, 96, 112),
            BorderThickness = 1,
            CornerRadius = 10,
            Padding = new Thickness(24),
            Child = body,
        }.Center());
        _about = about;
        Window.OverlayLayer.Add(about);
        close.Focus();
    }

    /// <summary>Close the About dialog.</summary>
    public void CloseAbout()
    {
        if (Window is null || _about is null)
        {
            return;
        }

        Window.OverlayLayer.Remove(_about);
        _about = null;
    }

    /// <summary>Save the dock layout to the settings folder (also done on leave).</summary>
    public void SaveLayout()
    {
        if (Dock is { } dock)
        {
            UserSettings.TryWrite(LayoutFile, dock.SaveLayout());
        }
    }

    /// <summary>Put the dock back to <see cref="DefaultLayout"/> (View > Reset Layout).</summary>
    public void ResetLayout()
    {
        if (Dock is null)
        {
            return;
        }

        if (StatsWindow is not null)
        {
            DockStats();
        }

        _viewport2Pane = null;
        Dock.LoadLayout(DefaultLayout);
        RebindPanes();
        ViewModel.Viewport2Visible.Set(_viewport2Pane is not null);
    }

    private static IconTemplate Icon(string name)
    {
        return new IconTemplate(size => DemoIcons.Icon(name, size.Dip) ?? (FrameworkElement)new TextBlock().Text(name[..1]));
    }

    private Window BuildWindow(DemoApp app)
    {
        OutlinerList = Outliner.Build(ViewModel);
        InspectorPanel = Inspector.Build(ViewModel);
        Stats = StatsPanel.Build();

        var dock = new DockingManager
        {
            ContentFactory = pane => pane.Component switch
            {
                "viewport" => ViewportA,
                "viewport2" => ViewportB,
                "outliner" => OutlinerList,
                "inspector" => InspectorPanel.Root,
                "stats" => StatsWindow is null ? Stats.Root : new TextBlock().Text("Shown in the stats window").Margin(10),
                _ => new TextBlock().Text(pane.Title ?? pane.Component ?? ""),
            },
        };
        Dock = dock;
        dock.LoadLayout(UserSettings.TryRead(LayoutFile) ?? DefaultLayout);
        RebindPanes();
        ViewModel.Viewport2Visible.Set(_viewport2Pane is not null);
        dock.PaneClosing += (_, e) =>
        {
            if (ReferenceEquals(e.Pane, _viewport2Pane))
            {
                _viewport2Pane = null;
                ViewModel.Viewport2Visible.Set(false);
            }
        };

        Window window = WorkbenchShell.Build(this, app, dock);
        return window;
    }

    // After a layout load the panes are new handles: find the ones the mode tracks by component key.
    private void RebindPanes()
    {
        _viewportPane = null;
        _viewport2Pane = null;
        _statsPane = null;
        if (Dock is null)
        {
            return;
        }

        foreach (DockPane p in Dock.DocumentPanes)
        {
            if (p.Component == "viewport")
            {
                _viewportPane = p;
            }
            else if (p.Component == "viewport2")
            {
                _viewport2Pane = p;
            }
        }

        foreach (DockPane p in Dock.Panes)
        {
            if (p.Component == "stats")
            {
                _statsPane = p;
            }
        }
    }

    private void DetachStats()
    {
        if (_app is null || Stats is null || StatsWindow is not null)
        {
            return;
        }

        // The chart leaves the dock first (an element lives in one tree), then shows in its own window.
        _statsPane?.Close();
        _statsPane = null;
        (int x, int y) = (80, 80);
        StatsWindow = _app.Host.OpenSecondary("MewStage Stats", x, y, 560, 320);
        StatsWindow.Background = WindowBackground;
        StatsWindow.Show(new Window { Padding = Thickness.Zero, StyleSheet = DemoStyles.CreateSheet() }.Content(Stats.Root));
        ViewModel.StatsDetached.Set(true);
    }

    private void DockStats()
    {
        if (_app is null || Stats is null || Dock is null)
        {
            return;
        }

        if (StatsWindow is { } w)
        {
            w.Show(new Window());   // release the chart from the window's tree before the window goes
            _app.Host.CloseSecondary(w);
            StatsWindow = null;
        }

        _statsPane ??= Dock.AddToolPane("Stats", Stats.Root, DockEdge.Bottom, "stats");
        ViewModel.StatsDetached.Set(false);
    }

    private void OnAfterUi(RenderGraph graph)
    {
        Compositor?.AddTerminalPasses(graph);
    }
}
