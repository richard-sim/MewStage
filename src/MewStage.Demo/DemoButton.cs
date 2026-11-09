// The demo's button: a Button whose look comes entirely from a ControlTemplate (a Border for the chrome, a
// ContentPresenter for the content) and from named styles (DemoStyles). The class itself adds nothing but a type to
// hang the type rule and the template on; a templated control draws no chrome of its own, so the Border part takes
// the control's Background / BorderBrush / BorderThickness / CornerRadius through ctx.BindChrome and the style's
// StateTriggers animate those properties through the binding.
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewStage.Demo;

/// <summary>A templated, style-driven button (see the file header and <see cref="DemoStyles"/>).</summary>
internal sealed class DemoButton : Button
{
    /// <summary>The template every <see cref="DemoButton"/> gets from its type rule: chrome Border + ContentPresenter.</summary>
    public static readonly ControlTemplate DefaultTemplate = new DelegateControlTemplate<DemoButton>(static (owner, ctx) =>
    {
        ContentPresenter presenter = new ContentPresenter().Center();
        var chrome = new Border { Child = presenter };
        ctx.BindChrome(chrome);
        ctx.Bind(chrome, PaddingProperty);
        ctx.Register("Chrome", chrome);
        return chrome;
    });

    /// <summary>A button with <paramref name="text"/> in the named style <paramref name="styleName"/>, bound to <paramref name="command"/>.</summary>
    public static DemoButton Create(string styleName, string text, Command? command = null)
    {
        var button = new DemoButton { StyleName = styleName };
        button.Content(text);
        button.Command = command;
        return button;
    }

    /// <summary>The chrome part, once the template is applied (null before the first layout).</summary>
    public Border? Chrome { get; private set; }

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Chrome = GetTemplateChild<Border>("Chrome");
    }
}
