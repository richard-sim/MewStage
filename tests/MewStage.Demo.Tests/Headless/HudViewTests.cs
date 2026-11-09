using Aprillz.MewUI;

using MewStage.Demo.Overlay;
using MewStage.Demo.Tests.Support;

using Xunit;

namespace MewStage.Demo.Tests.Headless;

/// <summary>
/// The HUD without a GPU: the readouts invalidate the retained window only when they change (a bound setter is the
/// invalidation), the hint's Transition runs under host pulses at fixed timestamps, and the 1920 x 1080 layout keeps
/// its far corner inside the design space whatever the surface size.
/// </summary>
public sealed class HudViewTests
{
    [Fact]
    public void Readouts_InvalidateOnlyWhenTheBoundTextChanges()
    {
        var vm = new HudViewModel();
        var hud = HudView.Build(vm);
        using var screen = new HeadlessScreen(1280, 720, designSpace: true);
        screen.Show(hud.Window);
        Assert.True(screen.Frame());
        Assert.Equal("-- ms", hud.FrameTimeText.Text);

        // Settled: an OnRequest frame has nothing to redraw.
        Assert.False(screen.FrameOnRequest());

        vm.SetReadouts(16.6, 1.0f);
        Assert.Equal("16.60 ms", vm.FrameTime.Value);
        Assert.Equal("57 deg", vm.Angle.Value);
        Assert.True(screen.FrameOnRequest(), "a changed readout did not invalidate the HUD");
        Assert.Equal("16.60 ms", hud.FrameTimeText.Text);

        vm.SetReadouts(16.6, 1.0f);   // the same strings: ObservableValue.Set reports no change, nothing invalidates
        Assert.False(screen.FrameOnRequest());
    }

    [Fact]
    public void HintFade_IsAPulsedTransition_ReadAtFixedTimestamps()
    {
        var hud = HudView.Build(new HudViewModel());
        using var screen = new HeadlessScreen(1280, 720, designSpace: true);
        screen.Show(hud.Window);
        screen.Frame();
        Assert.Equal(1.0, hud.Hint.Opacity);

        hud.FadeOutHint();
        screen.Frame(1);
        screen.Frame(1);
        screen.Frame(HudView.HintFadeMs / 2);
        double halfway = hud.Hint.Opacity;
        screen.Frame(HudView.HintFadeMs * 2);
        double done = hud.Hint.Opacity;
        Assert.True(halfway is > 0.01 and < 0.99, $"halfway through the fade the opacity is {halfway} (the eased transition should be mid-flight)");
        Assert.Equal(0.0, done);

        hud.ShowHint();
        screen.Frame(1);
        screen.Frame(HudView.HintFadeMs * 2);
        Assert.Equal(1.0, hud.Hint.Opacity);
    }

    [Theory]
    [InlineData(1280, 720)]
    [InlineData(1024, 600)]
    [InlineData(3840, 2160)]
    public void FarCornerMarker_SitsInsideTheDesignSpace(int width, int height)
    {
        var hud = HudView.Build(new HudViewModel());
        using var screen = new HeadlessScreen(width, height, designSpace: true);
        screen.Show(hud.Window);
        screen.Frame();

        Rect r = hud.CornerMarker.TranslateRect(new Rect(0, 0, hud.CornerMarker.Bounds.Width, hud.CornerMarker.Bounds.Height), hud.Window);
        Assert.Equal(new Size(HudView.DesignWidth, HudView.DesignHeight), hud.Window.ClientSize);
        Assert.True(r.Width > 100, $"the marker did not lay out: {r}");
        Assert.InRange(r.Right, HudView.DesignWidth - 60, HudView.DesignWidth - 40);
        Assert.InRange(r.Bottom, HudView.DesignHeight - 50, HudView.DesignHeight - 30);
    }
}
