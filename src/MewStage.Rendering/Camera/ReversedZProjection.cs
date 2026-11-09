// Reversed-Z [0, 1] projections and the world (Z-up) -> view (Y-up) basis change.
//
// CONVENTIONS (docs/architecture.md):
//   World frame: right-handed, +X right / +Y forward / +Z up; counter-clockwise front faces.
//   Clip depth range [0, 1]. REVERSED-Z: near -> 1, far -> 0, depth test Greater / GreaterEqual, clear depth 0.
//   The renderer applies ONE documented world (Z-up) -> view (Y-up) basis change, in the camera.
//   Per-API clip conventions: D3D12 clip +Y up; Vulkan framebuffer +Y down, so a Y-flip is baked into the projection.
//
// Why reversed-Z: pairing the float depth buffer's exponent distribution with 1/z's hyperbolic distribution yields
// near-uniform precision across the whole range, the standard fix for far-plane z-fighting.
//
// All matrices are System.Numerics.Matrix4x4 in the ROW-VECTOR convention (v' = v * M), composed left to right
// (world * View * Proj). System.Numerics stores row-major; the GPU reads row-major (DXC -Zpr) and the upload is a
// plain memcpy (no transpose).

using System;
using System.Numerics;

namespace MewStage.Rendering.Camera;

/// <summary>Which backend's clip-space convention to bake into a projection.</summary>
public enum ClipConvention : byte
{
    /// <summary>D3D12 and Metal: clip-space +Y up, depth [0, 1]. No Y-flip baked.</summary>
    D3DMetal = 0,

    /// <summary>Vulkan: the framebuffer's Y points down, so a Y-flip is baked in and world-up stays screen-up. Depth [0, 1].</summary>
    Vulkan = 1,
}

/// <summary>Builds reversed-Z [0, 1] projection matrices and the world-to-view basis change. Pure and headless-testable.
/// The matrices map near to 1 and far to 0; pair them with a Greater depth test and clear depth to 0. Row-vector.</summary>
public static class ReversedZProjection
{
    /// <summary>The one documented basis change: world (right-handed, +X right, +Y forward, +Z up) to view (right-handed,
    /// +X right, +Y up, -Z forward), so the camera looks down its -Z. Handedness is preserved (determinant +1).</summary>
    /// <returns>The basis-change matrix.</returns>
    public static Matrix4x4 WorldToViewBasis()
    {
        // Row-vector (v' = v * M): ROW i is how world axis i maps into view space.
        //   world +X (right)   -> view +X
        //   world +Y (forward) -> view -Z
        //   world +Z (up)      -> view +Y
        return new Matrix4x4(
            1, 0, 0, 0,
            0, 0, -1, 0,
            0, 1, 0, 0,
            0, 0, 0, 1);
    }

    /// <summary>A reversed-Z perspective projection (depth [0, 1], near to 1, far to 0) from a vertical field of view.
    /// Right-handed, looking down -Z. Row-vector.</summary>
    /// <param name="fovYRadians">Vertical field of view in radians (0 &lt; fovY &lt; pi).</param>
    /// <param name="aspect">Width / height.</param>
    /// <param name="near">Near plane (&gt; 0).</param>
    /// <param name="far">Far plane (&gt; near).</param>
    /// <param name="clip">The clip convention.</param>
    /// <returns>The projection matrix.</returns>
    public static Matrix4x4 Perspective(float fovYRadians, float aspect, float near, float far, ClipConvention clip = ClipConvention.D3DMetal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fovYRadians);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fovYRadians, MathF.PI);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(aspect);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(near);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(far, near);

        float g = 1f / MathF.Tan(fovYRadians * 0.5f);   // focal length (cotangent)

        // Row-vector [0, 1] right-handed perspective (clip = v_view * M; clip.w = -z_view):
        //   x' = (g / aspect) * x
        //   y' = g * y
        //   z' = a * z + b   (reversed-Z: near -> 1, far -> 0)
        //   w' = -z
        // The -1 sits in M34 and the depth bias in M43 (the transpose of the column-vector form).
        float a = near / (far - near);          // depth scale (reversed)
        float b = far * near / (far - near);    // depth bias (reversed)

        Matrix4x4 m = new(
            g / aspect, 0, 0, 0,
            0, g, 0, 0,
            0, 0, a, -1,
            0, 0, b, 0);

        if (clip == ClipConvention.Vulkan)
        {
            FlipClipY(ref m);
        }

        return m;
    }

    /// <summary>A reversed-Z orthographic projection (depth [0, 1], near to 1, far to 0). Right-handed, looking down -Z.
    /// Row-vector.</summary>
    /// <param name="width">View volume width.</param>
    /// <param name="height">View volume height.</param>
    /// <param name="near">Near plane.</param>
    /// <param name="far">Far plane (&gt; near).</param>
    /// <param name="clip">The clip convention.</param>
    /// <returns>The projection matrix.</returns>
    public static Matrix4x4 Orthographic(float width, float height, float near, float far, ClipConvention clip = ClipConvention.D3DMetal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(far, near);

        // Row-vector [0, 1] right-handed orthographic. Reversed-Z swaps near and far: z' = a * z + b with
        // a = 1 / (far - near), b = far / (far - near).
        float a = 1f / (far - near);
        float b = far / (far - near);
        Matrix4x4 m = new(
            2f / width, 0, 0, 0,
            0, 2f / height, 0, 0,
            0, 0, a, 0,
            0, 0, b, 1);

        if (clip == ClipConvention.Vulkan)
        {
            FlipClipY(ref m);
        }

        return m;
    }

    /// <summary>Maps a view-space distance in front of the camera (-z_view &gt; 0) to clip depth [0, 1] under the reversed-Z
    /// perspective: 1 at the near plane, 0 at the far plane. A test helper.</summary>
    /// <param name="viewDistance">Distance in front of the camera.</param>
    /// <param name="near">Near plane.</param>
    /// <param name="far">Far plane.</param>
    /// <returns>The clip depth.</returns>
    public static float PerspectiveDepth(float viewDistance, float near, float far)
    {
        float a = near / (far - near);
        float b = far * near / (far - near);

        // clip.z = a * z_view + b with z_view = -viewDistance; clip.w = -z_view = viewDistance.
        float clipZ = (a * -viewDistance) + b;
        return clipZ / viewDistance;
    }

    // Vulkan's framebuffer Y is down: negate clip-space Y. In row-vector form clip.y is column 2 (M12, M22, M32, M42).
    private static void FlipClipY(ref Matrix4x4 m)
    {
        m.M12 = -m.M12;
        m.M22 = -m.M22;
        m.M32 = -m.M32;
        m.M42 = -m.M42;
    }
}
