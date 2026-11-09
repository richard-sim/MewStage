using System;
using System.Numerics;

using MewStage.Rendering.Camera;

using Xunit;

namespace MewStage.Rendering.Tests;

/// <summary>
/// The camera conventions every shader and every piece of scene code relies on: row-major + row-vector matrices
/// (a point transforms as v * M, which is what System.Numerics' Transform computes), reversed-Z depth (near -> 1,
/// far -> 0), the world frame (+X right, +Y forward, +Z up) mapping to a view frame looking down -Z, and the per-backend
/// clip-Y convention (Vulkan flips).
/// </summary>
public sealed class CameraConventionTests
{
    private const float Tol = 1e-4f;

    private static Vector3 Xform(in Matrix4x4 m, Vector3 v)
    {
        var r = Vector4.Transform(new Vector4(v, 1f), m);   // v * M (row vector)
        return new Vector3(r.X, r.Y, r.Z);
    }

    private static Vector4 Clip(in Matrix4x4 m, Vector3 v)
    {
        return Vector4.Transform(new Vector4(v, 1f), m);
    }

    [Fact]
    public void WorldToView_RemapsAxes()
    {
        Matrix4x4 m = ReversedZProjection.WorldToViewBasis();
        Assert.True(Vector3.Distance(Xform(m, new Vector3(1, 0, 0)), new Vector3(1, 0, 0)) < Tol);   // right stays right
        Assert.True(Vector3.Distance(Xform(m, new Vector3(0, 1, 0)), new Vector3(0, 0, -1)) < Tol);  // forward -> down -Z
        Assert.True(Vector3.Distance(Xform(m, new Vector3(0, 0, 1)), new Vector3(0, 1, 0)) < Tol);   // up -> +Y
    }

    [Fact]
    public void WorldToView_PreservesHandedness()
    {
        Matrix4x4 m = ReversedZProjection.WorldToViewBasis();
        Assert.True(MathF.Abs(m.GetDeterminant() - 1f) < Tol);   // a rotation, not a reflection
    }

    [Fact]
    public void Perspective_NearMapsTo1_FarMapsTo0()
    {
        const float Near = 0.01f, Far = 100f;
        Assert.True(MathF.Abs(ReversedZProjection.PerspectiveDepth(Near, Near, Far) - 1f) < 1e-3f);
        Assert.True(MathF.Abs(ReversedZProjection.PerspectiveDepth(Far, Near, Far) - 0f) < 1e-3f);
    }

    [Fact]
    public void Perspective_DepthIsMonotonicDecreasing()
    {
        const float Near = 0.01f, Far = 100f;
        float dPrev = ReversedZProjection.PerspectiveDepth(Near, Near, Far);
        for (float d = 0.05f; d <= Far; d *= 1.5f)
        {
            float dep = ReversedZProjection.PerspectiveDepth(d, Near, Far);
            Assert.True(dep <= dPrev + 1e-5f, $"depth not decreasing at d={d}: {dep} > {dPrev}");
            Assert.InRange(dep, -1e-4f, 1f + 1e-4f);
            dPrev = dep;
        }
    }

    [Fact]
    public void Vulkan_FlipsClipY_RelativeToD3D()
    {
        Matrix4x4 d3d = ReversedZProjection.Perspective(1.0f, 1.7778f, 0.01f, 100f, ClipConvention.D3DMetal);
        Matrix4x4 vk = ReversedZProjection.Perspective(1.0f, 1.7778f, 0.01f, 100f, ClipConvention.Vulkan);
        Assert.True(MathF.Abs(d3d.M22 + vk.M22) < Tol && MathF.Abs(d3d.M22) > Tol);   // the Y column is negated
        Assert.Equal(d3d.M11, vk.M11, 4);   // X unchanged
        Assert.Equal(d3d.M33, vk.M33, 4);   // depth unchanged
    }

    [Fact]
    public void Perspective_RejectsBadArgs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReversedZProjection.Perspective(0f, 1.7f, 0.1f, 10f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ReversedZProjection.Perspective(1f, 1.7f, 1f, 1f));
    }

    [Fact]
    public void Camera_LookingForward_PutsAPointAheadInFrontAndCentered()
    {
        var cam = new RenderCamera { Position = new Vector3(0, -5, 0), Forward = new Vector3(0, 1, 0), Up = new Vector3(0, 0, 1) };
        Matrix4x4 vp = cam.ViewProjection(16f / 9f);
        Vector4 c = Clip(vp, Vector3.Zero);
        Assert.True(c.W > 0, "the origin is in front of the camera");
        Assert.True(MathF.Abs(c.X / c.W) < Tol && MathF.Abs(c.Y / c.W) < Tol, "the origin is on the view axis");
        float depth = c.Z / c.W;
        Assert.InRange(depth, 0f, 1f);

        // Reversed-Z: a nearer point has a LARGER depth value.
        Vector4 nearer = Clip(vp, new Vector3(0, -2, 0));
        Assert.True(nearer.Z / nearer.W > depth);
    }

    [Fact]
    public void Camera_WorldUp_IsScreenUp_AndWorldRight_IsScreenRight()
    {
        var cam = new RenderCamera { Position = new Vector3(0, -5, 0), Forward = new Vector3(0, 1, 0), Up = new Vector3(0, 0, 1), Clip = ClipConvention.D3DMetal };
        Matrix4x4 vp = cam.ViewProjection(1f);
        Vector4 up = Clip(vp, new Vector3(0, 0, 1));
        Vector4 right = Clip(vp, new Vector3(1, 0, 0));
        Assert.True(up.Y / up.W > 0.1f, "+Z world is up on screen (D3D clip Y up)");
        Assert.True(right.X / right.W > 0.1f, "+X world is right on screen");

        // Under the Vulkan convention the same point lands at a negative clip Y (framebuffer Y is down).
        cam.Clip = ClipConvention.Vulkan;
        Vector4 upVk = Clip(cam.ViewProjection(1f), new Vector3(0, 0, 1));
        Assert.True(upVk.Y / upVk.W < -0.1f);
    }

    [Fact]
    public void Camera_Orthographic_MapsHeightToClipRange()
    {
        var cam = new RenderCamera { Position = new Vector3(0, -5, 0), Forward = new Vector3(0, 1, 0), Up = new Vector3(0, 0, 1), Orthographic = true, OrthoHeight = 4f };
        Matrix4x4 vp = cam.ViewProjection(1f);
        Vector4 top = Clip(vp, new Vector3(0, 0, 2f));     // half the ortho height above the axis
        Assert.Equal(1f, top.W, 4);
        Assert.Equal(1f, top.Y, 3);
    }
}
