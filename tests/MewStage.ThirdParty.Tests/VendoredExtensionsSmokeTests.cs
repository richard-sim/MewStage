using Aprillz.MewUI;
using Aprillz.MewUI.Geometry;
using Aprillz.MewUI.MewDock;
using Aprillz.MewUI.Rendering;

using Xunit;

namespace MewStage.ThirdParty.Tests;

/// <summary>One smoke test per vendored MewUI extension: the vendored tree builds and each core entry point runs.
/// A broken vendor, a missing package or a patch that no longer applies fails here before anything else does.</summary>
public sealed class VendoredExtensionsSmokeTests
{
    [Fact]
    public void MewDock_PublicTypesAreAvailable_AndTheCaptionToggleExists()
    {
        Assert.NotNull(typeof(DockingManager).FullName);
        Assert.True(typeof(DockEdge).IsEnum);

        // The mewdock/0001 vendor patch adds ShowToolCaptions (default true = upstream behavior).
        Assert.NotNull(typeof(DockingManager).GetProperty("ShowToolCaptions"));
    }

    [Fact]
    public void Svg_ParsesDocumentViaCommittedGeneratedRegistry()
    {
        // Parsing 'rect' / 'circle' needs SvgElementFactory's element registry, which is the COMMITTED generated
        // output under extensions/MewUI.Svg/Generated, so this exercises that output rather than a build-time generator.
        const string Svg =
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">" +
            "<rect x=\"0\" y=\"0\" width=\"5\" height=\"5\"/><circle cx=\"7\" cy=\"7\" r=\"2\"/></svg>";
        var doc = global::Svg.SvgDocument.Parse(Svg);
        Assert.NotNull(doc);
        Assert.True(doc.HasChildren());
    }

    [Fact]
    public void Geometry_ComputesAreaAndHitTestsThroughClipper2()
    {
        var square = PathGeometry.FromRect(0, 0, 10, 10);
        Assert.Equal(100.0, PathGeometryOperations.GetArea(square), 6);
        Assert.True(PathGeometryOperations.FillContains(square, new Point(5, 5)));
        Assert.False(PathGeometryOperations.FillContains(square, new Point(15, 5)));
        var other = PathGeometry.FromRect(5, 5, 10, 10);
        Assert.True(PathGeometryOperations.Intersects(square, other));
    }

    [Fact]
    public void MewCharts_InitializesTheLiveChartsEngine()
    {
        LiveChartsCore.Kernel.LiveChartsSettings settings = Aprillz.MewUI.MewCharts.LiveChartsMewUI.EnsureInitialized();
        Assert.NotNull(settings);
        Assert.NotNull(typeof(Aprillz.MewUI.MewCharts.MewChartEngine).FullName);
    }

    [Fact]
    public void Preview_BootstrapTypeLoads()
    {
        // Preview (hot-reload previews from the IDE) is JIT-only by design; here only the type load is proven.
        Assert.NotNull(typeof(Aprillz.MewUI.Preview.PreviewBootstrap).GetMethod("TryRegister"));
    }
}
