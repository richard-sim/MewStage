// The cube scene as a viewport's world: the Viewport project's IViewportSceneRenderer adapted over the Scene
// project's CubeSceneRenderer (Viewport knows nothing about Scene; the demo joins them). One instance per viewport,
// all sharing one CubeScene, so two viewports show the same spinning cube from their own cameras. The compositor
// hands over the target's backend handle; this adapter imports it (state Common, the terminal pass's state) and
// draws into it with a clear.
using System;
using System.Numerics;

using MewStage.Graphics;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;
using MewStage.Scene;
using MewStage.Viewport;

namespace MewStage.Demo.Workbench;

/// <summary>The cube scene rendering into a viewport target (see the file header).</summary>
internal sealed class CubeViewportRenderer : IViewportSceneRenderer
{
    /// <summary>The viewport color format: RGBA8 UNorm, sampled by the `ui` pass as an RGBA image.</summary>
    public const Format TargetFormat = Format.R8G8B8A8_UNorm;

    private readonly CubeSceneRenderer _renderer;

    public CubeViewportRenderer(IGraphicsDevice device, SceneShaders shaders, CubeScene scene)
    {
        ArgumentNullException.ThrowIfNull(shaders);
        _renderer = new CubeSceneRenderer(device, shaders.CubeVs, shaders.CubePs, shaders.GridVs, shaders.GridPs, TargetFormat, scene);
    }

    /// <inheritdoc />
    public Format ColorFormat => TargetFormat;

    /// <summary>The frame whose upload ring the constants come from; the mode sets it before the compositor runs.</summary>
    public FrameContext? Frame { get; set; }

    /// <summary>The color the target is cleared to.</summary>
    public ClearColor Background { get; set; } = new(0.11f, 0.12f, 0.15f, 1f);

    /// <summary>Frames this viewport drew.</summary>
    public long FramesDrawn => _renderer.FramesDrawn;

    /// <inheritdoc />
    public ViewportPassOutput AddPasses(RenderGraph graph, in ViewportFrame frame)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (Frame is not { } ctx)
        {
            return default;
        }

        RgHandle target = graph.ImportTexture(
            new RgTextureDesc { Name = "viewport", Format = TargetFormat, Width = frame.Width, Height = frame.Height, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 },
            frame.TargetHandle,
            ResourceState.Common);
        Matrix4x4 viewProjection = _renderer.AddPasses(graph, target, frame.Width, frame.Height, frame.Camera, ctx, clear: true, Background);
        return new ViewportPassOutput(target, viewProjection);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _renderer.Dispose();
    }
}
