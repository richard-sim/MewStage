using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Graphics;
using MewStage.Input;
using MewStage.Rendering.Backends.Headless;

using Xunit;

namespace MewStage.Host.Tests.Input;

/// <summary>
/// The pixel -> DIP scale the layer hands its input bridge every frame: the design-fit k in design space (1920 x 1080
/// shown at 1280 x 720 is 2/3) and the display scale in DIP space (a 125 % monitor is 1.25). A wrong scale is a
/// 40-50 px miss on a 125 % monitor, so the rule is pinned here rather than in a hand test.
/// </summary>
public sealed class UiLayerScaleTests
{
    private sealed class FakeSurface : IUiSurface
    {
        public FakeSurface(IGraphicsDevice device, int w, int h, float scale)
        {
            Device = device;
            PixelWidth = w;
            PixelHeight = h;
            DisplayScale = scale;
        }

        public GpuBackend Backend => GpuBackend.Vulkan;

        public IGraphicsDevice Device { get; }

        public int FramesInFlight => 2;

        public int PixelWidth { get; set; }

        public int PixelHeight { get; set; }

        public float DisplayScale { get; set; }
    }

    [Fact]
    public void DesignSpace_ScaleIsTheDesignFit()
    {
        using var dev = new HeadlessDevice();
        var surface = new FakeSurface(dev, 1280, 720, 1.25f);
        using var layer = new UiLayer(surface, null, null, 1920, 1080);
        layer.SetWindow(new Window().Content(new Label().Text("hi")));
        Assert.NotNull(layer.Host);
        Assert.True(layer.Host.UseDesignSpace);

        layer.Dispatch(new RawInputSnapshot(), 0.016);
        Assert.Equal(1280f / 1920f, layer.Bridge.Scale, 5);   // the display scale plays no part in design space

        // A resize re-fits: 1920 x 1080 shown at 3840 x 2160 is k = 2.
        surface.PixelWidth = 3840;
        surface.PixelHeight = 2160;
        layer.Resize();
        layer.Dispatch(new RawInputSnapshot(), 0.016);
        Assert.Equal(2f, layer.Bridge.Scale, 5);
    }

    [Fact]
    public void DipSpace_ScaleIsTheDisplayScale()
    {
        using var dev = new HeadlessDevice();
        var surface = new FakeSurface(dev, 1600, 900, 1.25f);
        using var layer = new UiLayer(surface, null, null, 0, 0);
        layer.SetWindow(new Window().Content(new Label().Text("hi")));
        Assert.NotNull(layer.Host);
        Assert.False(layer.Host.UseDesignSpace);

        layer.Dispatch(new RawInputSnapshot(), 0.016);
        Assert.Equal(1.25f, layer.Bridge.Scale, 5);
        Assert.Equal(new Size(1280, 720), layer.Host.TopWindow!.ClientSize);   // 1600 / 1.25 DIP wide

        surface.DisplayScale = 2f;
        layer.Resize();
        layer.Dispatch(new RawInputSnapshot(), 0.016);
        Assert.Equal(2f, layer.Bridge.Scale, 5);
        Assert.Equal(new Size(800, 450), layer.Host.TopWindow!.ClientSize);
    }

    [Fact]
    public void SetDesignSpace_SwitchesBetweenSpaces_OnALiveHost()
    {
        using var dev = new HeadlessDevice();
        var surface = new FakeSurface(dev, 1280, 720, 1.25f);
        using var layer = new UiLayer(surface, null, null, 0, 0);
        layer.SetWindow(new Window().Content(new Label().Text("hi")));
        layer.Dispatch(new RawInputSnapshot(), 0.016);
        Assert.Equal(1.25f, layer.Bridge.Scale, 5);
        Assert.Equal(new Size(1024, 576), layer.Host!.TopWindow!.ClientSize);

        // A HUD screen takes the design space: the live host re-fits and the window becomes 1920 x 1080 DIP.
        layer.SetDesignSpace(1920, 1080);
        Assert.True(layer.Host.UseDesignSpace);
        Assert.Equal(1920, layer.DesignWidth);
        layer.Dispatch(new RawInputSnapshot(), 0.016);
        Assert.Equal(1280f / 1920f, layer.Bridge.Scale, 5);
        Assert.Equal(new Size(1920, 1080), layer.Host.TopWindow!.ClientSize);

        // Back to DIP for a tool screen.
        layer.SetDesignSpace(0, 0);
        Assert.False(layer.Host.UseDesignSpace);
        layer.Dispatch(new RawInputSnapshot(), 0.016);
        Assert.Equal(1.25f, layer.Bridge.Scale, 5);
        Assert.Equal(new Size(1024, 576), layer.Host.TopWindow!.ClientSize);
    }

    [Fact]
    public void EmptyStack_DispatchesNothing_AndRendersNoPass()
    {
        using var dev = new HeadlessDevice();
        var surface = new FakeSurface(dev, 640, 360, 1f);
        using var layer = new UiLayer(surface, null, null, 0, 0);
        Assert.Null(layer.Host);
        Assert.Null(layer.TopWindow);
        Assert.False(layer.WantsInput);
        layer.Dispatch(new RawInputSnapshot(), 0.016);   // no host yet: a no-op
        var graph = new Rendering.Graph.RenderGraph();
        Assert.False(layer.Render(graph, default, clearBackground: true));

        layer.SetWindow(new Window().Content(new Label().Text("hi")));
        Assert.NotNull(layer.TopWindow);
        layer.SetWindow(null);
        Assert.Null(layer.TopWindow);
        Assert.False(layer.Render(graph, default, clearBackground: true));
    }
}
