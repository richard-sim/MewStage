// The Workbench's chrome around the dock: the menu bar, the toolbar (the same commands as the menus, as icon buttons),
// the status bar and the window that carries the command handlers and the keyboard gestures (Ctrl+Q quit, Ctrl+2
// Viewport 2, Ctrl+Shift+S stats window, Escape closes About).
using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.MewDock;

namespace MewStage.Demo.Workbench;

/// <summary>Builds the Workbench window around a dock (see the file header).</summary>
internal static class WorkbenchShell
{
    /// <summary>The window for <paramref name="mode"/>: menu bar, toolbar, <paramref name="dock"/>, status bar; handlers and gestures registered.</summary>
    public static Window Build(WorkbenchMode mode, DemoApp app, DockingManager dock)
    {
        ArgumentNullException.ThrowIfNull(mode);
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(dock);
        WorkbenchViewModel vm = mode.ViewModel;

        Menu fileMenu = new Menu().Item(WorkbenchMode.StartPageCommand).Separator().Item(WorkbenchMode.QuitCommand);
        var viewMenu = new Menu();
        viewMenu.Add(new MenuItem().Command(WorkbenchMode.ToggleViewport2Command).IsCheckable().BindIsChecked(vm.Viewport2Visible));
        viewMenu.Add(new MenuItem().Command(WorkbenchMode.StatsWindowCommand).IsCheckable().BindIsChecked(vm.StatsDetached));
        viewMenu.Separator().Item(WorkbenchMode.ResetLayoutCommand);
        Menu helpMenu = new Menu().Item(WorkbenchMode.AboutCommand);
        MenuBar menuBar = new MenuBar().Items(new MenuItem("_File").Menu(fileMenu), new MenuItem("_View").Menu(viewMenu), new MenuItem("_Help").Menu(helpMenu));

        ToolBar toolBar = new ToolBar().Band(
            new ToolBarGroup().Item(WorkbenchMode.StartPageCommand).Item(WorkbenchMode.QuitCommand),
            new ToolBarGroup().Toggle(WorkbenchMode.ToggleViewport2Command).Toggle(WorkbenchMode.StatsWindowCommand).Item(WorkbenchMode.ResetLayoutCommand),
            new ToolBarGroup().Item(Inspector.FrameCameraACommand).Item(WorkbenchMode.AboutCommand));

        Border statusBar = new Border { Padding = new Thickness(10, 4, 10, 4), BorderThickness = 1, BorderBrush = Color.FromRgb(60, 64, 76) }
            .Child(new TextBlock().FontSize(12).BindText(vm.Status));

        var root = new DockPanel { LastChildFill = true };
        root.Add(menuBar.DockTop());
        root.Add(toolBar.DockTop());
        root.Add(statusBar.DockBottom());
        root.Add(dock);

        Window window = new Window { Padding = Thickness.Zero, StyleSheet = DemoStyles.CreateSheet() }.Content(root);
        window.Title("MewStage Workbench");
        window.Commands.Register(WorkbenchMode.QuitCommand, () => app.Quit());
        window.Commands.Register(WorkbenchMode.StartPageCommand, () => app.Switch(new StartPage()));
        window.Commands.Register(WorkbenchMode.ToggleViewport2Command, () => mode.SetViewport2(!vm.Viewport2Visible.Value));
        window.Commands.Register(WorkbenchMode.StatsWindowCommand, mode.ToggleStatsWindow);
        window.Commands.Register(WorkbenchMode.ResetLayoutCommand, mode.ResetLayout);
        window.Commands.Register(WorkbenchMode.AboutCommand, mode.ShowAbout);
        window.Commands.Register(Inspector.FrameCameraACommand, mode.FrameCameraA);
        window.Commands.Register(Inspector.FrameCameraBCommand, mode.FrameCameraB);
        window.InputMap.Map(WorkbenchMode.QuitCommand, new KeyGesture(Key.Q, ModifierKeys.Control));
        window.InputMap.Map(WorkbenchMode.ToggleViewport2Command, new KeyGesture(Key.D2, ModifierKeys.Control));
        window.InputMap.Map(WorkbenchMode.StatsWindowCommand, new KeyGesture(Key.S, ModifierKeys.Control | ModifierKeys.Shift));   // not Ctrl+Shift+T: MewUI's DevTools own that one
        window.InputMap.Map(new KeySequence(new KeyGesture(Key.Escape)), mode.CloseAbout, () => mode.IsAboutOpen);
        return window;
    }
}
