// The pause menu: the SECOND window on the Overlay stack. A translucent scrim over the whole design space (the HUD
// and the world show through, darkened) and an opaque centered panel: a ComboBox (cube color; its list opens inside
// the surface), a Slider (spin speed; the gamepad's engage model), a ToggleSwitch (floor grid), a TextBox (a label
// the HUD shows; text input reaches it through the SDL text path) and the DemoButtons. Handlers for the navigation
// commands sit on the window (the router's last stop for a key pressed with nothing focused); the buttons bind the
// same commands, so a click, Enter, Escape, B and Start all run one handler. While this window is the top of the stack it alone gets input (the modal trap).
using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Ui.Navigation;

namespace MewStage.Demo.Overlay;

/// <summary>The pause window (see the file header).</summary>
internal sealed class PauseMenu
{
    /// <summary>Leave Overlay mode for the start page.</summary>
    public static readonly Command ToStartCommand = new("demo.pause.start", "_Start page");

    /// <summary>Ask to quit (opens the confirmation dialog).</summary>
    public static readonly Command QuitCommand = new("demo.pause.quit", "_Quit");

    private PauseMenu(Window window, Border panel, ComboBox colors, Slider spin, ToggleSwitch grid, TextBox label, DemoButton resume, DemoButton quit)
    {
        Window = window;
        Panel = panel;
        Colors = colors;
        Spin = spin;
        Grid = grid;
        Label = label;
        Resume = resume;
        QuitButton = quit;
    }

    /// <summary>The menu window, pushed on the stack above the HUD (the top window takes the input).</summary>
    public Window Window { get; }

    /// <summary>The opaque centered panel.</summary>
    public Border Panel { get; }

    /// <summary>The cube color ComboBox.</summary>
    public ComboBox Colors { get; }

    /// <summary>The spin speed Slider.</summary>
    public Slider Spin { get; }

    /// <summary>The floor grid ToggleSwitch.</summary>
    public ToggleSwitch Grid { get; }

    /// <summary>The HUD label TextBox.</summary>
    public TextBox Label { get; }

    /// <summary>The Resume button (bound to <see cref="NavCommands.Confirm"/>).</summary>
    public DemoButton Resume { get; }

    /// <summary>The Quit button (bound to <see cref="QuitCommand"/>).</summary>
    public DemoButton QuitButton { get; }

    /// <summary>Build the menu over <paramref name="vm"/>; the callbacks run the screen's actions.</summary>
    /// <param name="vm">The shared state.</param>
    /// <param name="resume">Close the menu.</param>
    /// <param name="toStart">Leave for the start page.</param>
    /// <param name="quit">Open the quit confirmation.</param>
    public static PauseMenu Build(HudViewModel vm, Action resume, Action toStart, Action quit)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ArgumentNullException.ThrowIfNull(resume);
        ArgumentNullException.ThrowIfNull(toStart);
        ArgumentNullException.ThrowIfNull(quit);

        ComboBox colors = new ComboBox().Items(HudViewModel.ColorNames).BindSelectedIndex(vm.ColorIndex).Width(300).FontSize(20);
        Slider spin = new Slider().Minimum(0).Maximum(3).BindValue(vm.SpinSpeed).Width(300);
        ToggleSwitch grid = new ToggleSwitch().BindIsChecked(vm.ShowGrid);
        TextBox label = new TextBox().BindText(vm.Label).Placeholder("Shown on the HUD").Width(300).FontSize(20);

        Grid form = new Grid().Columns("Auto,*").Rows("Auto,Auto,Auto,Auto").Spacing(18);
        form.Add(FormLabel("Cube color").Row(0).Column(0));
        form.Add(colors.Row(0).Column(1).Left());
        form.Add(FormLabel("Spin speed").Row(1).Column(0));
        form.Add(spin.Row(1).Column(1).Left().CenterVertical());
        form.Add(FormLabel("Floor grid").Row(2).Column(0));
        form.Add(grid.Row(2).Column(1).Left().CenterVertical());
        form.Add(FormLabel("HUD label").Row(3).Column(0));
        form.Add(label.Row(3).Column(1).Left());

        var resumeButton = DemoButton.Create(DemoStyles.Primary, "Resume", NavCommands.Confirm);
        StackPanel buttons = new StackPanel().Horizontal().Spacing(16);
        buttons.Add(resumeButton);
        buttons.Add(DemoButton.Create(DemoStyles.Ghost, "Start page", ToStartCommand));
        var quitButton = DemoButton.Create(DemoStyles.Ghost, "Quit", QuitCommand);
        buttons.Add(quitButton);

        StackPanel body = new StackPanel().Vertical().Spacing(30);
        body.Add(new TextBlock().Text("Paused").FontFamily(DemoFonts.Display).FontSize(40).FontWeight(FontWeight.Bold));
        body.Add(form);
        body.Add(buttons.Right());

        Border panel = new Border
        {
            Background = Color.FromRgb(30, 33, 41),
            BorderBrush = Color.FromRgb(70, 76, 92),
            BorderThickness = 1,
            CornerRadius = 14,
            Padding = new Thickness(40),
            Child = body,
        }.Width(640);

        Grid root = new Grid().Width(HudView.DesignWidth).Height(HudView.DesignHeight);
        root.Add(new Border { Background = Color.FromArgb(150, 0, 0, 0) });
        root.Add(panel.Center());

        Window window = new Window
        {
            Padding = Thickness.Zero,
            AllowsTransparency = true,
            Background = Color.Transparent,
            StyleSheet = DemoStyles.CreateSheet(),
        }.Content(root);
        window.Title("MewStage pause");
        window.FontSize(20);

        window.Commands.Register(NavCommands.Confirm, resume);
        window.Commands.Register(NavCommands.Back, resume);
        window.Commands.Register(ToStartCommand, toStart);
        window.Commands.Register(QuitCommand, quit);
        NavCommands.MapKeys(window);
        return new PauseMenu(window, panel, colors, spin, grid, label, resumeButton, quitButton);
    }

    private static TextBlock FormLabel(string text)
    {
        return new TextBlock().Text(text).FontSize(20).Foreground(Color.FromRgb(190, 196, 208)).CenterVertical();
    }
}
