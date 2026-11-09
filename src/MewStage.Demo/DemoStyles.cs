// The demo's style sheet: the DemoButton type rule (template + base look) and the two named button styles,
// "Demo.Button.Primary" (filled accent) and "Demo.Button.Ghost" (outline), each with StateTriggers for Hot /
// Focused / Pressed / Disabled and Transitions so a state change fades rather than snaps. Colors come from the theme
// through Theme resolvers, so the same sheet works on the dark Overlay HUD and in Kit mode's light window.
//
// Pitfall this file is built around (handoff section 17): a value assigned in a constructor is LOCAL and beats every
// style setter and trigger for good, so nothing here is set on the element; everything goes through setters.
using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewStage.Demo;

/// <summary>The demo's shared styles (see the file header).</summary>
internal static class DemoStyles
{
    /// <summary>The filled accent button.</summary>
    public const string Primary = "Demo.Button.Primary";

    /// <summary>The outline button.</summary>
    public const string Ghost = "Demo.Button.Ghost";

    /// <summary>Milliseconds a state change fades over.</summary>
    public const int FadeMs = 140;

    /// <summary>A fresh sheet with the demo's rules (attach one to each window: <c>window.StyleSheet = DemoStyles.CreateSheet()</c>).</summary>
    public static StyleSheet CreateSheet()
    {
        var sheet = new StyleSheet();
        sheet.Define<DemoButton>(ButtonBase());
        sheet.Define(Primary, PrimaryButton);
        sheet.Define(Ghost, GhostButton);
        return sheet;
    }

    /// <summary>The type rule every <see cref="DemoButton"/> gets: the template, padding, corner radius and the fades.</summary>
    public static Style ButtonBase()
    {
        return new Style(typeof(DemoButton))
        {
            Setters =
            [
                Setter.Create(Control.TemplateProperty, (ControlTemplate?)DemoButton.DefaultTemplate),
                Setter.Create(Control.PaddingProperty, new Thickness(22, 10, 22, 10)),
                Setter.Create(Control.CornerRadiusProperty, 8.0),
                Setter.Create(Control.BorderThicknessProperty, 2.0),
            ],
            Transitions =
            [
                Transition.Create(Control.BackgroundProperty, FadeMs),
                Transition.Create(Control.BorderBrushProperty, FadeMs),
                Transition.Create(TextElement.ForegroundProperty, FadeMs),
            ],
        };
    }

    /// <summary>"Demo.Button.Primary": accent fill, accent text; hover lightens, press darkens, focus draws a ring, disabled grays.</summary>
    public static Style PrimaryButton()
    {
        return new Style(typeof(DemoButton))
        {
            BasedOn = ButtonBase(),
            Setters =
            [
                Setter.Create(Control.BackgroundProperty, t => t.Palette.Accent),
                Setter.Create(Control.BorderBrushProperty, t => t.Palette.Accent),
                Setter.Create(TextElement.ForegroundProperty, t => t.Palette.AccentText),
            ],
            Triggers =
            [
                new StateTrigger
                {
                    Match = VisualStateFlags.Hot,
                    Setters = [Setter.Create(Control.BackgroundProperty, t => Lighten(t.Palette.Accent, 0.12))],
                },
                new StateTrigger
                {
                    Match = VisualStateFlags.Focused,
                    Setters = [Setter.Create(Control.BorderBrushProperty, t => t.Palette.Focus)],
                },
                new StateTrigger
                {
                    Match = VisualStateFlags.Pressed,
                    Setters = [Setter.Create(Control.BackgroundProperty, t => Darken(t.Palette.Accent, 0.18))],
                },
                new StateTrigger
                {
                    Exclude = VisualStateFlags.Enabled,
                    Setters =
                    [
                        Setter.Create(Control.BackgroundProperty, t => t.Palette.ButtonDisabledBackground),
                        Setter.Create(Control.BorderBrushProperty, t => t.Palette.ButtonDisabledBackground),
                        Setter.Create(TextElement.ForegroundProperty, t => t.Palette.DisabledText),
                    ],
                },
            ],
        };
    }

    /// <summary>"Demo.Button.Ghost": transparent fill with an outline; hover tints the fill, focus brightens the outline.</summary>
    public static Style GhostButton()
    {
        return new Style(typeof(DemoButton))
        {
            BasedOn = ButtonBase(),
            Setters =
            [
                Setter.Create(Control.BackgroundProperty, Color.Transparent),
                Setter.Create(Control.BorderBrushProperty, t => t.Palette.WindowText.WithAlpha(110)),
                Setter.Create(TextElement.ForegroundProperty, t => t.Palette.WindowText),
            ],
            Triggers =
            [
                new StateTrigger
                {
                    Match = VisualStateFlags.Hot,
                    Setters = [Setter.Create(Control.BackgroundProperty, t => t.Palette.WindowText.WithAlpha(28))],
                },
                new StateTrigger
                {
                    Match = VisualStateFlags.Focused,
                    Setters = [Setter.Create(Control.BorderBrushProperty, t => t.Palette.Focus)],
                },
                new StateTrigger
                {
                    Match = VisualStateFlags.Pressed,
                    Setters = [Setter.Create(Control.BackgroundProperty, t => t.Palette.WindowText.WithAlpha(60))],
                },
                new StateTrigger
                {
                    Exclude = VisualStateFlags.Enabled,
                    Setters =
                    [
                        Setter.Create(Control.BorderBrushProperty, t => t.Palette.DisabledText.WithAlpha(90)),
                        Setter.Create(TextElement.ForegroundProperty, t => t.Palette.DisabledText),
                    ],
                },
            ],
        };
    }

    /// <summary>Move <paramref name="c"/> toward white by <paramref name="amount"/> (0..1).</summary>
    public static Color Lighten(Color c, double amount)
    {
        return c.Lerp(Color.FromArgb(c.A, 255, 255, 255), Math.Clamp(amount, 0, 1));
    }

    /// <summary>Move <paramref name="c"/> toward black by <paramref name="amount"/> (0..1).</summary>
    public static Color Darken(Color c, double amount)
    {
        return c.Lerp(Color.FromArgb(c.A, 0, 0, 0), Math.Clamp(amount, 0, 1));
    }
}
