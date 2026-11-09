using System.Numerics;

namespace MewStage.Scene;

/// <summary>
/// What the demo lets the user change about the cube scene. Plain mutable state the UI's view models write and the
/// renderer reads every frame; nothing here invalidates anything (the scene redraws every frame anyway).
/// </summary>
public sealed class SceneSettings
{
    /// <summary>Rotation speed about the cube's spin axes, radians per second.</summary>
    public float SpinSpeed { get; set; } = 0.8f;

    /// <summary>Uniform scale of the cube (edge length, metres).</summary>
    public float Scale { get; set; } = 1f;

    /// <summary>Multiplies the face colors (1, 1, 1 = as authored).</summary>
    public Vector3 Tint { get; set; } = Vector3.One;

    /// <summary>Draw the cube's edges only.</summary>
    public bool Wireframe { get; set; }

    /// <summary>Show the floor grid.</summary>
    public bool ShowGrid { get; set; } = true;

    /// <summary>Light azimuth around +Z, radians (the default lights the faces the demo's camera sees).</summary>
    public float LightYaw { get; set; } = -2.2f;

    /// <summary>Light elevation above the floor, radians.</summary>
    public float LightPitch { get; set; } = 0.75f;

    /// <summary>Flat ambient term, 0..1.</summary>
    public float Ambient { get; set; } = 0.35f;

    /// <summary>The unit direction toward the light for the current yaw / pitch.</summary>
    public Vector3 LightDirection()
    {
        float cp = (float)System.Math.Cos(LightPitch);
        return Vector3.Normalize(new Vector3((float)System.Math.Cos(LightYaw) * cp, (float)System.Math.Sin(LightYaw) * cp, (float)System.Math.Sin(LightPitch)));
    }
}
