// The start page: an embedded MewUI window in DIP space on a cleared backbuffer (no world), three DemoButtons bound
// to commands, a line about each mode and the backend in the status line. Kit mode cannot start in this process
// (upstream's platform host registers once per process and takes the whole loop), so its button relaunches the exe
// with --standard and closes this window.
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Graphics;
using MewStage.Rendering.Graph;

namespace MewStage.Demo;

/// <summary>The mode picker (see the file header).</summary>
internal sealed class StartPage : IDemoMode
{
    /// <summary>Enter Overlay mode.</summary>
    public static readonly Command OverlayCommand = new("demo.start.overlay", "_Overlay");

    /// <summary>Enter Workbench mode.</summary>
    public static readonly Command WorkbenchCommand = new("demo.start.workbench", "_Workbench");

    /// <summary>Relaunch into Kit mode.</summary>
    public static readonly Command KitCommand = new("demo.start.kit", "_Kit");

    private DemoApp? _app;

    /// <summary>The page's window (null until entered).</summary>
    public Window? Window { get; private set; }

    /// <inheritdoc />
    public void Enter(DemoApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _app = app;
        app.Host.Ui.SetDesignSpace(0, 0);
        app.Host.UiClearsBackground = true;
        app.Host.Background = new ClearColor(0.07f, 0.08f, 0.10f, 1f);

        Window = Build(app);
        app.Host.Ui.SetWindow(Window);
    }

    /// <inheritdoc />
    public void Tick(double dt)
    {
    }

    /// <inheritdoc />
    public void Render(RenderGraph graph, RgHandle backbuffer)
    {
        // Nothing under the UI: the `ui` pass clears to the background.
    }

    /// <inheritdoc />
    public void Leave()
    {
        _app?.Host.Ui.SetWindow(null);
        Window?.Close();   // dispose the page's tree (out of the stack already); a re-entered start page builds a new one
        Window = null;
        _app = null;
    }

    /// <summary>Build the page for <paramref name="app"/> (public so a test can lay it out without a GPU window).</summary>
    public static Window Build(DemoApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        TextBlock title = new TextBlock().Text("MewStage").FontFamily(DemoFonts.Display).FontSize(46).FontWeight(FontWeight.Bold);
        TextBlock tagline = new TextBlock().Text("MewUI and MewVG inside an app that owns its window, GPU device and render graph.").FontSize(16).TextWrapping(TextWrapping.Wrap);
        tagline.Foreground(Color.FromRgb(170, 176, 188));

        StackPanel buttons = new StackPanel().Vertical().Spacing(18);
        buttons.Add(ModeRow(
            DemoButton.Create(DemoStyles.Primary, "Overlay", OverlayCommand).Width(220),
            "A spinning cube and a game-style HUD in one backbuffer: the UI composites over the world, pause and dialog windows stack on top."));
        buttons.Add(ModeRow(
            DemoButton.Create(DemoStyles.Ghost, "Workbench", WorkbenchCommand).Width(220),
            "The same cube inside a docked tool window: viewports as UI images, menus, an inspector, a stats chart."));
        buttons.Add(ModeRow(
            DemoButton.Create(DemoStyles.Ghost, "Kit", KitCommand).Width(220),
            "MewUI on its own window host, no app device: the control kit page, DevTools and Preview. Restarts the demo."));

        TextBlock status = new TextBlock().Text($"GPU: {app.Options.Backend}{(app.Options.Warp ? " (WARP)" : "")}   |   {app.Host.Gpu.Device.GetType().Name}").FontSize(13);
        status.Foreground(Color.FromRgb(120, 126, 140));

        StackPanel column = new StackPanel().Vertical().Spacing(26).MaxWidth(760);
        column.Add(title);
        column.Add(tagline);
        column.Add(buttons);
        column.Add(status);

        var root = new Grid { Padding = new Thickness(48) };
        root.Add(column.Center());
        Window window = new Window { Padding = Thickness.Zero, StyleSheet = DemoStyles.CreateSheet() }.Content(root);
        window.Title("MewStage");

        window.Commands.Register(OverlayCommand, () => app.Switch(new Overlay.OverlayMode()));
        window.Commands.Register(WorkbenchCommand, () => app.Switch(new Workbench.WorkbenchMode()));
        window.Commands.Register(KitCommand, () => RelaunchForKit(app));
        return window;
    }

    private static Grid ModeRow(DemoButton button, string description)
    {
        TextBlock text = new TextBlock().Text(description).FontSize(14).TextWrapping(TextWrapping.Wrap);
        text.Foreground(Color.FromRgb(190, 196, 208));
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid row = new Grid().Columns("Auto,*").Spacing(24);
        row.Add(button.Column(0).CenterVertical());
        row.Add(text.Column(1));
        return row;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A relaunch that fails (no process path, a sandbox that forbids it) is reported on stderr and the page stays usable.")]
    private static void RelaunchForKit(DemoApp app)
    {
        string? exe = Environment.ProcessPath;
        if (exe is null)
        {
            Console.Error.WriteLine("[start] cannot relaunch for Kit mode: no process path");
            return;
        }

        var info = new ProcessStartInfo(exe) { UseShellExecute = false };
        info.ArgumentList.Add("--standard");
        foreach (string a in app.Options.BackendArgs())
        {
            info.ArgumentList.Add(a);
        }

        try
        {
            using var p = Process.Start(info);
            if (p is null)
            {
                Console.Error.WriteLine("[start] cannot relaunch for Kit mode: Process.Start returned null");
                return;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[start] cannot relaunch for Kit mode: " + ex.Message);
            return;
        }

        app.Quit();
    }
}
