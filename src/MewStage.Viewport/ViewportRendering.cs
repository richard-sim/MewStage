// The world side of a viewport: what renders into a viewport's offscreen target each frame. One renderer instance per
// viewport (a renderer's per-target resources, a depth buffer say, are sized to that viewport, never to the swapchain).
using System;
using System.Numerics;

using MewStage.Graphics;
using MewStage.Rendering.Camera;
using MewStage.Rendering.Graph;

namespace MewStage.Viewport;

/// <summary>The per-frame inputs of a viewport's world render.</summary>
public readonly struct ViewportFrame
{
    /// <summary>Creates a frame description.</summary>
    public ViewportFrame(ulong targetHandle, uint width, uint height, RenderCamera camera, float timeSeconds)
    {
        TargetHandle = targetHandle;
        Width = width;
        Height = height;
        Camera = camera;
        TimeSeconds = timeSeconds;
    }

    /// <summary>Backend handle of the viewport's color target (state Common at import; see <see cref="ViewportCompositor"/>).</summary>
    public ulong TargetHandle { get; }

    /// <summary>Target width, pixels.</summary>
    public uint Width { get; }

    /// <summary>Target height, pixels.</summary>
    public uint Height { get; }

    /// <summary>The viewport camera.</summary>
    public RenderCamera Camera { get; }

    /// <summary>Seconds for time-driven shading (0 in deterministic tests).</summary>
    public float TimeSeconds { get; }

    /// <summary>Width / height.</summary>
    public float Aspect => Height == 0 ? 1f : (float)Width / Height;
}

/// <summary>What a world render left in this frame's graph.</summary>
public readonly struct ViewportPassOutput
{
    /// <summary>Creates an output record.</summary>
    public ViewportPassOutput(RgHandle color, Matrix4x4 viewProjection)
    {
        Color = color;
        ViewProjection = viewProjection;
    }

    /// <summary>The color target (RenderTarget state after the last world pass).</summary>
    public RgHandle Color { get; }

    /// <summary>The row-vector view-projection the world was drawn with (labels project with the same one).</summary>
    public Matrix4x4 ViewProjection { get; }
}

/// <summary>Renders a world into a viewport target.</summary>
public interface IViewportSceneRenderer : IDisposable
{
    /// <summary>The color format the renderer writes (the viewport target is created in it).</summary>
    Format ColorFormat { get; }

    /// <summary>
    /// Adds this frame's world passes. The renderer imports <see cref="ViewportFrame.TargetHandle"/> itself (state
    /// Common) and leaves it in RenderTarget; a default (invalid) color handle means nothing was drawn this frame.
    /// </summary>
    ViewportPassOutput AddPasses(RenderGraph graph, in ViewportFrame frame);
}

/// <summary>Projection helpers shared by the control's labels and any picking an application adds.</summary>
public static class ViewportMath
{
    /// <summary>
    /// Projects a world point with a row-vector view-projection to viewport-local coordinates (origin top-left, y
    /// down) of a <paramref name="width"/> x <paramref name="height"/> viewport. False when behind the camera.
    /// <paramref name="clipYUp"/>: true for the D3D / Metal convention (clip +Y up), false for Vulkan (clip +Y down).
    /// </summary>
    public static bool Project(Vector3 world, in Matrix4x4 viewProjection, float width, float height, bool clipYUp, out Vector2 screen)
    {
        var c = Vector4.Transform(new Vector4(world, 1f), viewProjection);
        if (c.W <= 0f)
        {
            screen = default;
            return false;
        }

        float nx = c.X / c.W;
        float ny = c.Y / c.W;
        float sy = clipYUp ? (1f - ny) * 0.5f : (1f + ny) * 0.5f;
        screen = new Vector2((nx + 1f) * 0.5f * width, sy * height);
        return true;
    }
}
