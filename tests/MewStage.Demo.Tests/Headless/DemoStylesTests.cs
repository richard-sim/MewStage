using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Demo.Tests.Support;

using Xunit;

namespace MewStage.Demo.Tests.Headless;

/// <summary>
/// The ControlTemplate + styles + triggers evidence: the named styles resolve from a window's StyleSheet with no
/// Application running (the embedded hosts never start one), the type rule supplies the template, a trigger changes
/// the chrome's color through the template binding, and a Transition animates it under host pulses.
/// </summary>
public sealed class DemoStylesTests
{
    [Fact]
    public void Sheet_DefinesBothNamedStyles_AndTheTypeRule()
    {
        StyleSheet sheet = DemoStyles.CreateSheet();
        Style primary = Assert.IsType<Style>(sheet.Get(DemoStyles.Primary));
        Style ghost = Assert.IsType<Style>(sheet.Get(DemoStyles.Ghost));
        Style type = Assert.IsType<Style>(sheet.GetByType(typeof(DemoButton)));
        Assert.Equal(typeof(DemoButton), primary.TargetType);
        Assert.Equal(typeof(DemoButton), ghost.TargetType);
        Assert.NotNull(primary.BasedOn);
        Assert.Equal(4, primary.Triggers.Count);
        Assert.Equal(4, ghost.Triggers.Count);
        Assert.NotNull(type.FindTransition(Control.BackgroundProperty.Id));
        Assert.Contains(type.Setters, s => s.Property == Control.TemplateProperty);
    }

    [Fact]
    public void PrimaryButton_ResolvesWithoutAnApplication_AndTheTemplateOwnsTheChrome()
    {
        using var screen = new HeadlessScreen(640, 360);
        var button = DemoButton.Create(DemoStyles.Primary, "Go");
        var panel = new Canvas();
        panel.Add(button.CanvasPosition(20, 20));
        Window window = new Window { StyleSheet = DemoStyles.CreateSheet() }.Content(panel);
        screen.Show(window);
        Assert.True(screen.Frame());

        Assert.Same(DemoButton.DefaultTemplate, button.Template);
        Assert.NotNull(button.Chrome);
        Assert.NotEqual(0, button.Chrome.Background.A);   // the accent fill reached the chrome part through BindChrome
        Assert.Equal(button.Background, button.Chrome.Background);
        Assert.Equal(8.0, button.Chrome.CornerRadius);
        Assert.True(button.Bounds.Width > 40 && button.Bounds.Height > 20, $"the templated button has no size: {button.Bounds}");
    }

    [Fact]
    public void DisabledTrigger_ChangesTheChrome_ThroughAPulsedTransition()
    {
        using var screen = new HeadlessScreen(640, 360);
        var button = DemoButton.Create(DemoStyles.Primary, "Go");
        var panel = new Canvas();
        panel.Add(button.CanvasPosition(20, 20));
        screen.Show(new Window { StyleSheet = DemoStyles.CreateSheet() }.Content(panel));
        screen.Frame();
        Color enabled = button.Background;

        button.IsEnabled = false;
        screen.Frame(1);   // the trigger resolves, the transition's clock starts
        screen.Frame(1);
        screen.Frame(DemoStyles.FadeMs / 2);
        Color halfway = button.Background;
        screen.Frame(DemoStyles.FadeMs * 2);
        Color disabled = button.Background;

        Assert.NotEqual(enabled, disabled);
        Assert.NotEqual(enabled, halfway);
        Assert.NotEqual(disabled, halfway);
        Assert.Equal(disabled, button.Chrome!.Background);   // the part follows the owner to the end
    }

    [Fact]
    public void GhostButton_IsTransparentUntilHovered()
    {
        using var screen = new HeadlessScreen(640, 360);
        var button = DemoButton.Create(DemoStyles.Ghost, "Ghost");
        var panel = new Canvas();
        panel.Add(button.CanvasPosition(20, 20));
        screen.Show(new Window { StyleSheet = DemoStyles.CreateSheet() }.Content(panel));
        screen.Frame();
        Assert.Equal(0, button.Background.A);
        Assert.NotEqual(0, button.BorderBrush.A);
    }
}
