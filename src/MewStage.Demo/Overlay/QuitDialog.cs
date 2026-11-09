// The quit confirmation: the THIRD window on the Overlay stack (a 0.6 scrim over the paused menu, a small panel, two
// buttons). Its root starts fully transparent and OverlayMode sets Opacity = 1 the frame after it is shown, so the
// element Transition fades the whole dialog in through MewVG's group-opacity path. Escape, B and the Cancel button
// run one Back handler; Enter activates the focused button (the navigator focuses Cancel first).
using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Ui.Navigation;

namespace MewStage.Demo.Overlay;

/// <summary>The quit dialog window (see the file header).</summary>
internal sealed class QuitDialog
{
    /// <summary>Really quit.</summary>
    public static readonly Command ConfirmQuitCommand = new("demo.quit.confirm", "_Quit");

    /// <summary>Milliseconds the dialog fades in over.</summary>
    public const int FadeMs = 250;

    private QuitDialog(Window window, Grid root, DemoButton cancel, DemoButton quit)
    {
        Window = window;
        Root = root;
        Cancel = cancel;
        Quit = quit;
    }

    /// <summary>The dialog window, pushed on the stack above the pause menu.</summary>
    public Window Window { get; }

    /// <summary>The root whose <see cref="UIElement.Opacity"/> fades in.</summary>
    public Grid Root { get; }

    /// <summary>The Cancel button (bound to <see cref="NavCommands.Back"/>).</summary>
    public DemoButton Cancel { get; }

    /// <summary>The Quit button.</summary>
    public DemoButton Quit { get; }

    /// <summary>Start the fade-in (call once the window is on the stack).</summary>
    public void FadeIn()
    {
        Root.Opacity = 1;
    }

    /// <summary>Build the dialog; <paramref name="cancel"/> closes it, <paramref name="confirm"/> quits.</summary>
    public static QuitDialog Build(Action cancel, Action confirm)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        ArgumentNullException.ThrowIfNull(confirm);

        var cancelButton = DemoButton.Create(DemoStyles.Ghost, "Cancel", NavCommands.Back);
        var quitButton = DemoButton.Create(DemoStyles.Primary, "Quit", ConfirmQuitCommand);
        StackPanel buttons = new StackPanel().Horizontal().Spacing(16);
        buttons.Add(cancelButton);
        buttons.Add(quitButton);

        StackPanel body = new StackPanel().Vertical().Spacing(26);
        body.Add(new TextBlock().Text("Quit MewStage?").FontFamily(DemoFonts.Display).FontSize(32).FontWeight(FontWeight.Bold));
        body.Add(new TextBlock().Text("The window closes and the cube stops spinning.").FontSize(20).Foreground(Color.FromRgb(190, 196, 208)));
        body.Add(buttons.Right());

        Border panel = new Border
        {
            Background = Color.FromRgb(36, 39, 48),
            BorderBrush = Color.FromRgb(90, 96, 112),
            BorderThickness = 1,
            CornerRadius = 14,
            Padding = new Thickness(36),
            Child = body,
        }.Width(560);

        Grid root = new Grid().Width(HudView.DesignWidth).Height(HudView.DesignHeight);
        root.Add(new Border { Background = Color.FromArgb(153, 0, 0, 0) });
        root.Add(panel.Center());
        root.Opacity = 0;
        root.Transitions = [Transition.Create(UIElement.OpacityProperty, FadeMs)];

        Window window = new Window
        {
            Padding = Thickness.Zero,
            AllowsTransparency = true,
            Background = Color.Transparent,
            StyleSheet = DemoStyles.CreateSheet(),
        }.Content(root);
        window.Title("MewStage quit");
        window.FontSize(20);

        window.Commands.Register(NavCommands.Back, cancel);
        window.Commands.Register(ConfirmQuitCommand, confirm);
        NavCommands.MapKeys(window);
        return new QuitDialog(window, root, cancelButton, quitButton);
    }
}
