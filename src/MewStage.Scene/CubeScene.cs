using System;
using System.Numerics;

namespace MewStage.Scene;

/// <summary>
/// The state of the cube scene that is independent of any camera or target: the user settings and the spin angle.
/// One instance is shared by every <see cref="CubeSceneRenderer"/> that shows the scene (two viewports draw the same
/// cube at the same angle from their own cameras).
/// </summary>
public sealed class CubeScene
{
    /// <summary>The user-facing settings (spin speed, scale, tint, wireframe, grid, light).</summary>
    public SceneSettings Settings { get; } = new();

    /// <summary>The cube's current rotation about its spin axis, radians (advanced by <see cref="Advance"/>).</summary>
    public float Angle { get; private set; }

    /// <summary>Advance the animation by <paramref name="dt"/> seconds.</summary>
    public void Advance(double dt)
    {
        Angle = (float)((Angle + (Settings.SpinSpeed * dt)) % (Math.PI * 2));
    }

    /// <summary>The cube's world matrix for the current angle and settings (a rotation about two axes plus a uniform scale, lifted so it sits on the floor).</summary>
    public Matrix4x4 World()
    {
        float s = Settings.Scale;
        return Matrix4x4.CreateScale(s)
            * Matrix4x4.CreateRotationX(Angle * 0.7f)
            * Matrix4x4.CreateRotationZ(Angle)
            * Matrix4x4.CreateTranslation(0f, 0f, s * 0.9f);
    }

    /// <summary>The center of the cube in world space (what a viewport frames).</summary>
    public Vector3 Center => new(0f, 0f, Settings.Scale * 0.9f);

    /// <summary>The radius of the cube's bounding sphere.</summary>
    public float Radius => Settings.Scale * 0.87f;
}
