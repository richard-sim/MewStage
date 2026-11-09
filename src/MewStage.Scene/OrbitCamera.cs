using System;
using System.Numerics;

using MewStage.Rendering.Camera;

namespace MewStage.Scene;

/// <summary>
/// A camera that orbits a target: yaw around world +Z (up), pitch above the floor, at a distance. It produces a
/// <see cref="RenderCamera"/> every frame (world frame: +X right, +Y forward, +Z up; reversed-Z projection in the
/// backend's clip convention), and takes the viewport gestures (orbit, zoom, pan) as deltas.
/// </summary>
public sealed class OrbitCamera
{
    private const float MinPitch = -1.45f;
    private const float MaxPitch = 1.45f;
    private const float MinDistance = 0.5f;
    private const float MaxDistance = 60f;

    /// <summary>The point the camera looks at (world).</summary>
    public Vector3 Target { get; set; }

    /// <summary>Rotation around +Z, radians; 0 looks along +Y.</summary>
    public float Yaw { get; set; } = -0.6f;

    /// <summary>Elevation above the floor plane, radians (clamped short of straight up / down).</summary>
    public float Pitch
    {
        get;
        set => field = Math.Clamp(value, MinPitch, MaxPitch);
    } = 0.45f;

    /// <summary>Distance from the target, metres (clamped).</summary>
    public float Distance
    {
        get;
        set => field = Math.Clamp(value, MinDistance, MaxDistance);
    } = 4f;

    /// <summary>Vertical field of view, radians.</summary>
    public float FovY { get; set; } = 0.9f;

    /// <summary>The eye position for the current yaw / pitch / distance.</summary>
    public Vector3 Eye
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            var back = new Vector3(MathF.Sin(Yaw) * cp, -MathF.Cos(Yaw) * cp, MathF.Sin(Pitch));
            return Target + (back * Distance);
        }
    }

    /// <summary>Orbit by screen deltas (radians per unit).</summary>
    public void Orbit(float dYaw, float dPitch)
    {
        Yaw += dYaw;
        Pitch += dPitch;
    }

    /// <summary>Zoom by a wheel notch count: each notch scales the distance by 0.9 (in) or 1 / 0.9 (out).</summary>
    public void Zoom(float notches)
    {
        Distance *= MathF.Pow(0.9f, notches);
    }

    /// <summary>Pan the target in the view plane by screen deltas scaled to the distance.</summary>
    public void Pan(float dx, float dy)
    {
        RenderCamera cam = ToRenderCamera(ClipConvention.D3DMetal);
        var f = Vector3.Normalize(cam.Forward);
        var right = Vector3.Normalize(Vector3.Cross(f, Vector3.UnitZ));
        var up = Vector3.Cross(right, f);
        float k = Distance * 0.0025f;
        Target += (right * (-dx * k)) + (up * (dy * k));
    }

    /// <summary>Frame <paramref name="radius"/> (a bounding sphere) at the target so it fills most of the view.</summary>
    public void Frame(Vector3 target, float radius)
    {
        Target = target;
        Distance = radius / MathF.Sin(FovY * 0.5f) * 1.2f;
    }

    /// <summary>The render camera for this frame, in the backend's clip convention.</summary>
    public RenderCamera ToRenderCamera(ClipConvention clip)
    {
        Vector3 eye = Eye;
        return new RenderCamera
        {
            Position = eye,
            Forward = Vector3.Normalize(Target - eye),
            Up = Vector3.UnitZ,
            FovYRadians = FovY,
            Near = 0.05f,
            Far = 200f,
            Clip = clip,
        };
    }
}
