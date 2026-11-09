using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Geometry;
using Aprillz.MewUI.Rendering;

using MewStage.Demo.Kit;
using MewStage.Demo.Tests.Support;
using MewStage.Ui.Backend;

using Xunit;

namespace MewStage.Demo.Tests.Headless;

/// <summary>
/// The kit page without a platform host: every tab builds and renders on the embedded host (the page is a plain
/// Window), the geometry sample's four Boolean combinations have the areas set theory predicts, and the DevTools
/// gate: open in a Debug JIT build (the in-window inspector and performance monitor toggle on the embedded host; the
/// two window-opening tools are unmapped there), closed in Release.
/// </summary>
public sealed class KitTests
{
    [Fact]
    public void KitPage_BuildsSevenTabs_AndEveryTabRendersOnTheEmbeddedHost()
    {
        using var screen = new HeadlessScreen(1280, 800);
        var page = KitPage.Build();
        screen.Show(page.Window);
        Assert.True(screen.Frame());
        Assert.Equal(KitPage.TabNames.Length, page.Tabs.Tabs.Count);
        for (int i = 0; i < KitPage.TabNames.Length; i++)
        {
            Assert.Equal(KitPage.TabNames[i], page.Tabs.Tabs[i].HeaderText);
        }

        for (int i = 0; i < KitPage.TabNames.Length; i++)
        {
            page.Tabs.SelectedIndex = i;
            screen.Frame();
            Assert.True(screen.Frame(), "tab " + KitPage.TabNames[i] + " did not render");
        }

        // The geometry tab rendered: the sample combined its shapes.
        Assert.NotNull(page.Geometry.Result(0));
        Assert.Equal(Workbench.SceneObjectKind.Cube, page.InspectorModel.Selected);
    }

    [Fact]
    public void GeometrySample_FourCombinations_HaveTheAreasSetTheoryPredicts()
    {
        const double Size = GeometrySample.Cell;
        (RectangleGeometry rectShape, EllipseGeometry ellipseShape) = GeometrySample.Shapes(Size);
        double rect = rectShape.GetArea();
        double ellipse = ellipseShape.GetArea();
        Assert.InRange(rect, (0.25 * Size * Size) - 1, (0.25 * Size * Size) + 1);
        Assert.InRange(ellipse, Math.PI * 0.27 * 0.27 * Size * Size * 0.99, Math.PI * 0.27 * 0.27 * Size * Size);   // flattened: a hair under pi r^2
        double union = GeometrySample.Combine(GeometryCombineMode.Union, Size).GetArea();
        double intersect = GeometrySample.Combine(GeometryCombineMode.Intersect, Size).GetArea();
        double xor = GeometrySample.Combine(GeometryCombineMode.Xor, Size).GetArea();
        double exclude = GeometrySample.Combine(GeometryCombineMode.Exclude, Size).GetArea();
        Assert.True(intersect > 0, "the shapes overlap");
        Assert.InRange(union, Math.Max(rect, ellipse), rect + ellipse);
        Assert.InRange(intersect, 0, Math.Min(rect, ellipse));
        Assert.InRange(union, rect + ellipse - intersect - 2, rect + ellipse - intersect + 2);   // |A u B| = |A| + |B| - |A n B|
        Assert.InRange(xor, union - intersect - 2, union - intersect + 2);
        Assert.InRange(exclude, rect - intersect - 2, rect - intersect + 2);
    }

    [Fact]
    public void DevToolsGate_FollowsTheBuild()
    {
        using var screen = new HeadlessScreen(640, 400);
        Window window = new Window().Content(new TextBlock().Text("kit"));
        screen.Show(window);
        screen.Frame();
#if DEBUG
        Assert.True(WindowDevTools.IsSupported, "a Debug JIT build hosts the DevTools");
        Assert.Equal(KitPage.DevToolsAvailable, KitPage.Build().Status.Text);
        WindowDevTools tools = Assert.IsType<WindowDevTools>(window.DevTools);

        // The in-window tools work on the embedded host.
        tools.ToggleInspector();
        Assert.True(tools.InspectorIsVisible);
        Assert.True(screen.Frame());
        tools.TogglePerformanceMonitor();
        Assert.True(tools.PerformanceMonitorIsVisible);
        Assert.True(screen.Frame());
        tools.ToggleInspector();
        tools.TogglePerformanceMonitor();
        Assert.False(tools.InspectorIsVisible || tools.PerformanceMonitorIsVisible);
        Assert.True(screen.Frame());

        // The embedded host removed the two gestures that would open OS windows; the in-window ones are still mapped.
        Assert.False(window.InputMap.Unmap(new KeyGesture(Key.T, ModifierKeys.Primary | ModifierKeys.Shift)));
        Assert.False(window.InputMap.Unmap(new KeyGesture(Key.P, ModifierKeys.Primary | ModifierKeys.Shift | ModifierKeys.Alt)));
        Assert.True(window.InputMap.Unmap(new KeyGesture(Key.I, ModifierKeys.Primary | ModifierKeys.Shift)));
        Assert.True(window.InputMap.Unmap(new KeyGesture(Key.P, ModifierKeys.Primary | ModifierKeys.Shift)));
#else
        Assert.False(WindowDevTools.IsSupported, "a Release build has no DevTools");
        Assert.Null(window.DevTools);
        Assert.Equal(KitPage.DevToolsUnavailable, KitPage.Build().Status.Text);
#endif
        EmbeddedUiHost.UnmapWindowOpeningDevTools(window);   // a no-op the second time (and in Release)
    }
}
