using System;
using System.Numerics;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;

using MewStage.Demo.Tests.Support;
using MewStage.Graphics;
using MewStage.Rendering.Camera;
using MewStage.Rendering.Graph;
using MewStage.Ui.Backend;
using MewStage.Viewport;

using Xunit;

namespace MewStage.Demo.Tests.Headless;

/// <summary>
/// The viewport bridge without a GPU: projection in both clip conventions, the compositor's per-target bookkeeping
/// (allocate on the first laid-out frame, re-allocate on a size change with a new generation and image, release on
/// detach so the external-texture registry returns to where it was) and a frame's graph with the terminal pass
/// compiling without a state mismatch.
/// </summary>
public sealed class ViewportTests
{
    /// <summary>A world renderer that imports the target and writes it in one empty pass.</summary>
    private sealed class FlatRenderer : IViewportSceneRenderer
    {
        private RgHandle _target;

        public int Passes { get; private set; }

        public Format ColorFormat => Format.R8G8B8A8_UNorm;

        public ViewportPassOutput AddPasses(RenderGraph graph, in ViewportFrame frame)
        {
            _target = graph.ImportTexture(new RgTextureDesc { Name = "flat", Format = ColorFormat, Width = frame.Width, Height = frame.Height, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 }, frame.TargetHandle, ResourceState.Common);
            graph.AddPass("flat", PassKind.Graphics, s =>
            {
                s.Write(_target, ResourceState.RenderTarget);
                s.SetExecute(static (in _) => { });
            });
            Passes++;
            return new ViewportPassOutput(_target, frame.Camera.ViewProjection(frame.Aspect));
        }

        public void Dispose()
        {
        }
    }

    private static RenderCamera Camera()
    {
        return new RenderCamera { Position = new Vector3(0, -4, 2), Forward = Vector3.Normalize(new Vector3(0, 4, -2)), Up = Vector3.UnitZ, FovYRadians = 0.9f, Near = 0.05f, Far = 100f, Clip = ClipConvention.Vulkan };
    }

    [Fact]
    public void Project_PutsTheLookAtPointInTheMiddle_AndFlipsYPerConvention()
    {
        RenderCamera cam = Camera();
        Matrix4x4 vp = cam.ViewProjection(1.5f);
        Assert.True(ViewportMath.Project(Vector3.Zero, in vp, 300, 200, clipYUp: false, out Vector2 center));
        Assert.InRange(center.X, 149, 151);
        Assert.InRange(center.Y, 99, 101);

        // A point above the look-at point is higher on screen in both conventions once the flip is applied.
        var above = new Vector3(0, 0, 0.8f);
        Assert.True(ViewportMath.Project(above, in vp, 300, 200, clipYUp: false, out Vector2 vk));
        RenderCamera d3d = Camera();
        d3d.Clip = ClipConvention.D3DMetal;
        Matrix4x4 vpD3d = d3d.ViewProjection(1.5f);
        Assert.True(ViewportMath.Project(above, in vpD3d, 300, 200, clipYUp: true, out Vector2 dx));
        Assert.True(vk.Y < 100 && dx.Y < 100, $"the point above the center projected below it: vk {vk}, d3d {dx}");
        Assert.InRange(Math.Abs(vk.Y - dx.Y), 0, 0.5);

        // Behind the camera: no projection.
        Assert.False(ViewportMath.Project(cam.Position - (cam.Forward * 3f), in vp, 300, 200, false, out _));
    }

    [Fact]
    public void Compositor_AllocatesReallocatesAndReleases_AndTheFrameCompiles()
    {
        int registered = ExternalTextures.Count;
        using var screen = new HeadlessScreen(640, 360);
        var viewport = new ViewportControl { Camera = Camera() };
        var panel = new Canvas();
        panel.Add(viewport.Width(300).Height(200).CanvasPosition(10, 10));
        screen.Show(new Window().Content(panel));
        screen.Frame();
        Assert.True(viewport.IsRenderable);
        Assert.Equal((300, 200), (viewport.TargetPixelWidth, viewport.TargetPixelHeight));

        using var compositor = new ViewportCompositor(screen.Device, screen.Host, tightReadbackRows: true, framesInFlight: 2);
        var renderer = new FlatRenderer();
        compositor.Attach(viewport, renderer);
        Assert.True(compositor.IsAttached(viewport));
        Assert.Throws<InvalidOperationException>(() => compositor.Attach(viewport, renderer));

        // Frame 1: the target is allocated to the laid-out size, declared to the UI host, the control invalidated.
        var graph = new RenderGraph();
        compositor.AddViewportPasses(graph, 0f);
        Assert.Equal(1, compositor.RenderedLastFrame);
        ViewportTarget target = compositor.GetTarget(viewport);
        Assert.Equal(1, target.Generation);
        Assert.Equal((300, 200), (target.Width, target.Height));
        Assert.Same(target.Image, viewport.Image);
        Assert.Equal(1, viewport.FrameChangeCount);
        Assert.Equal(registered + 1, ExternalTextures.Count);
        compositor.AddTerminalPasses(graph);
        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(graph);   // the terminal pass's Common matches the next import: no mismatch
        Assert.NotNull(compiled);

        // A size change re-allocates: a new generation and a new image; the old texture goes through deferred destroy.
        viewport.Width(400);
        screen.Frame();
        IImage? firstImage = viewport.Image;
        var graph2 = new RenderGraph();
        compositor.AddViewportPasses(graph2, 0.016f);
        Assert.Equal(2, target.Generation);
        Assert.Equal(400, target.Width);
        Assert.NotSame(firstImage, viewport.Image);
        Assert.Equal(registered + 1, ExternalTextures.Count);

        // Hidden: nothing renders, the target stays.
        viewport.IsVisible = false;
        screen.Frame();
        var graph3 = new RenderGraph();
        compositor.AddViewportPasses(graph3, 0.032f);
        Assert.Equal(0, compositor.RenderedLastFrame);
        Assert.Equal(2, target.Generation);
        viewport.IsVisible = true;

        // Detach releases the image and the registry entry.
        compositor.Detach(viewport);
        Assert.False(compositor.IsAttached(viewport));
        Assert.Null(viewport.Image);
        Assert.Equal(registered, ExternalTextures.Count);
        Assert.Equal(2, renderer.Passes);
    }

    [Fact]
    public void Compositor_RendersNothing_ForAViewportWithoutACamera()
    {
        using var screen = new HeadlessScreen(320, 200);
        var viewport = new ViewportControl();
        screen.Show(new Window().Content(viewport));
        screen.Frame();
        using var compositor = new ViewportCompositor(screen.Device, screen.Host, true, 2);
        compositor.Attach(viewport, new FlatRenderer());
        compositor.AddViewportPasses(new RenderGraph(), 0f);
        Assert.Equal(0, compositor.RenderedLastFrame);
        Assert.Equal(0, compositor.GetTarget(viewport).Generation);
    }

    [Fact]
    public void Labels_ProjectWithTheLastViewProjection()
    {
        using var screen = new HeadlessScreen(640, 360);
        var viewport = new ViewportControl { Camera = Camera() };
        viewport.Labels.Add(new ViewportLabel(Vector3.Zero, "Cube", Color.White));
        viewport.InvalidateLabels();
        screen.Show(new Window { Padding = Thickness.Zero }.Content(viewport));
        Assert.True(screen.Frame());   // renders (and lays the label out) without a GPU
        viewport.LastViewProjection = viewport.Camera.ViewProjection((float)(viewport.Bounds.Width / viewport.Bounds.Height));
        viewport.NotifyFrameChanged();
        Assert.True(screen.Frame());
        Assert.Single(viewport.Labels);
    }
}
