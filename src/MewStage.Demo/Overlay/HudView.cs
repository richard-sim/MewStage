// The HUD: a transparent, read-only MewUI window laid out at a fixed 1920 x 1080 DIP design space (the host scales it
// to fit the backbuffer and maps the pointer through the same scale). Top-left the readouts, top-right a legend of
// three controller prompts drawn with Svg icons, bottom-center a hint that fades out after a few seconds through an
// element Transition the host pulses, and a marker in the far bottom-right corner (the design-space evidence: at
// k < 1 the whole 1920 x 1080 layout is drawn, letterboxed on the far edges).
using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewStage.Demo.Overlay;

/// <summary>The Overlay HUD window (see the file header).</summary>
internal sealed class HudView
{
    /// <summary>The design-space width, DIP.</summary>
    public const double DesignWidth = 1920;

    /// <summary>The design-space height, DIP.</summary>
    public const double DesignHeight = 1080;

    /// <summary>Milliseconds the hint's opacity fades over.</summary>
    public const int HintFadeMs = 600;

    private static readonly Color Dim = Color.FromRgb(176, 182, 196);
    private static readonly Color Bright = Color.FromRgb(240, 242, 246);

    private HudView(Window window, TextBlock hint, TextBlock cornerMarker, TextBlock frameTimeText)
    {
        Window = window;
        Hint = hint;
        CornerMarker = cornerMarker;
        FrameTimeText = frameTimeText;
    }

    /// <summary>The window to put at the bottom of the stack.</summary>
    public Window Window { get; }

    /// <summary>The "Esc: menu" hint (its <see cref="UIElement.Opacity"/> animates through a Transition).</summary>
    public TextBlock Hint { get; }

    /// <summary>The far-corner marker.</summary>
    public TextBlock CornerMarker { get; }

    /// <summary>The frame-time readout (bound to <see cref="HudViewModel.FrameTime"/>).</summary>
    public TextBlock FrameTimeText { get; }

    /// <summary>Start the hint's fade (0.6 s to transparent).</summary>
    public void FadeOutHint()
    {
        Hint.Opacity = 0;
    }

    /// <summary>Show the hint again (used when the menu closes).</summary>
    public void ShowHint()
    {
        Hint.Opacity = 1;
    }

    /// <summary>Build the HUD over <paramref name="vm"/>.</summary>
    public static HudView Build(HudViewModel vm)
    {
        Grid root = BuildRoot(vm, out TextBlock hint, out TextBlock marker, out TextBlock frameTime);
        Window window = new Window
        {
            Padding = Thickness.Zero,
            AllowsTransparency = true,
            Background = Color.Transparent,
            StyleSheet = DemoStyles.CreateSheet(),
        }.Content(root);
        window.Title("MewStage HUD");
        return new HudView(window, hint, marker, frameTime);
    }

    /// <summary>The HUD's 1920 x 1080 panel alone (the Kit page shows it at 1:1 inside a scroller).</summary>
    public static Grid BuildRoot(HudViewModel vm, out TextBlock hint, out TextBlock marker, out TextBlock frameTime)
    {
        ArgumentNullException.ThrowIfNull(vm);
        Grid root = new Grid().Width(DesignWidth).Height(DesignHeight);

        // Top-left: title + readouts bound to the view model (a changed string invalidates the window).
        StackPanel readouts = new StackPanel().Vertical().Spacing(6);
        readouts.Add(new TextBlock().Text("MewStage / Overlay").FontFamily(DemoFonts.Display).FontSize(40).FontWeight(FontWeight.Bold).Foreground(Bright));
        readouts.Add(Readout("frame", vm.FrameTime, out frameTime));
        readouts.Add(Readout("angle", vm.Angle, out _));
        readouts.Add(new TextBlock().FontSize(26).Foreground(Bright).BindText(vm.Label).Margin(0, 12, 0, 0));
        root.Add(readouts.Left().Top().Margin(48, 40, 0, 0));

        // Top-right: the legend (three prompts, Svg icons).
        StackPanel legend = new StackPanel().Vertical().Spacing(10);
        legend.Add(Prompt("play", "A / Enter", "select"));
        legend.Add(Prompt("close", "B / Esc", "back"));
        legend.Add(Prompt("pause", "Start / Esc", "menu"));
        root.Add(legend.Right().Top().Margin(0, 40, 48, 0));

        // Bottom-center: the hint, faded out by OverlayMode after a few seconds.
        hint = new TextBlock().Text("Esc or Start: menu").FontSize(28).Foreground(Bright);
        hint.Transitions = [Transition.Create(UIElement.OpacityProperty, HintFadeMs)];
        root.Add(hint.CenterHorizontal().Bottom().Margin(0, 0, 0, 56));

        // Far bottom-right: the design-space marker.
        marker = new TextBlock().Text("1920 x 1080 design space").FontSize(18).Foreground(Dim);
        root.Add(marker.Right().Bottom().Margin(0, 0, 48, 36));
        return root;
    }

    private static StackPanel Readout(string name, ObservableValue<string> value, out TextBlock text)
    {
        StackPanel row = new StackPanel().Horizontal().Spacing(14);
        row.Add(new TextBlock().Text(name).FontSize(24).Foreground(Dim).Width(90));
        text = new TextBlock().FontSize(24).Foreground(Bright).BindText(value);
        row.Add(text);
        return row;
    }

    private static StackPanel Prompt(string icon, string keys, string action)
    {
        StackPanel row = new StackPanel().Horizontal().Spacing(14);
        if (DemoIcons.Icon(icon, 36) is { } image)
        {
            row.Add(image.CenterVertical());
        }

        row.Add(new TextBlock().Text(keys).FontSize(24).Foreground(Bright).Width(170).CenterVertical());
        row.Add(new TextBlock().Text(action).FontSize(24).Foreground(Dim).CenterVertical());
        return row;
    }
}
