// The Workbench's state as observable values: what the inspector edits (two-way), what the outliner selects, what the
// status bar and the camera panels read. Apply() pushes the scene settings into the shared CubeScene each frame, so
// an edit shows in BOTH viewports the next frame.
using System;
using System.Globalization;

using Aprillz.MewUI;

using MewStage.Scene;

namespace MewStage.Demo.Workbench;

/// <summary>What the outliner lists.</summary>
internal enum SceneObjectKind
{
    /// <summary>The spinning cube.</summary>
    Cube,

    /// <summary>The directional light.</summary>
    Light,

    /// <summary>The first viewport's camera.</summary>
    CameraA,

    /// <summary>The second viewport's camera.</summary>
    CameraB,
}

/// <summary>One outliner entry.</summary>
internal sealed record SceneObject(string Name, string Icon, SceneObjectKind Kind);

/// <summary>Observable Workbench state (see the file header).</summary>
internal sealed class WorkbenchViewModel
{
    /// <summary>The scene objects, in outliner order.</summary>
    public static readonly SceneObject[] Objects =
    [
        new("Cube", "new", SceneObjectKind.Cube),
        new("Light", "settings", SceneObjectKind.Light),
        new("Camera A", "camera", SceneObjectKind.CameraA),
        new("Camera B", "camera", SceneObjectKind.CameraB),
    ];

    /// <summary>Spin speed, radians per second (0..3).</summary>
    public ObservableValue<double> SpinSpeed { get; } = new(0.8);

    /// <summary>Cube edge length (0.25..3).</summary>
    public ObservableValue<double> Scale { get; } = new(1.0);

    /// <summary>Index into <see cref="CubeColors.Names"/>.</summary>
    public ObservableValue<int> ColorIndex { get; } = new(0);

    /// <summary>Draw the cube's edges only.</summary>
    public ObservableValue<bool> Wireframe { get; } = new(false);

    /// <summary>Show the floor grid.</summary>
    public ObservableValue<bool> ShowGrid { get; } = new(true);

    /// <summary>Light azimuth, radians.</summary>
    public ObservableValue<double> LightYaw { get; } = new(-2.2);

    /// <summary>Light elevation, radians.</summary>
    public ObservableValue<double> LightPitch { get; } = new(0.75);

    /// <summary>The outliner's selection (index into <see cref="Objects"/>).</summary>
    public ObservableValue<int> SelectedIndex { get; } = new(0);

    /// <summary>Whether the second viewport is shown.</summary>
    public ObservableValue<bool> Viewport2Visible { get; } = new(false);

    /// <summary>Whether the stats chart lives in its own OS window.</summary>
    public ObservableValue<bool> StatsDetached { get; } = new(false);

    /// <summary>The status bar text.</summary>
    public ObservableValue<string> Status { get; } = new("");

    /// <summary>Camera A's readout for the inspector.</summary>
    public ObservableValue<string> CameraAInfo { get; } = new("");

    /// <summary>Camera B's readout for the inspector.</summary>
    public ObservableValue<string> CameraBInfo { get; } = new("");

    /// <summary>The selected object's kind.</summary>
    public SceneObjectKind Selected => Objects[Math.Clamp(SelectedIndex.Value, 0, Objects.Length - 1)].Kind;

    /// <summary>Push the editable values into <paramref name="scene"/>.</summary>
    public void Apply(CubeScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        SceneSettings s = scene.Settings;
        s.SpinSpeed = (float)SpinSpeed.Value;
        s.Scale = (float)Scale.Value;
        s.Tint = CubeColors.Tint(ColorIndex.Value);
        s.Wireframe = Wireframe.Value;
        s.ShowGrid = ShowGrid.Value;
        s.LightYaw = (float)LightYaw.Value;
        s.LightPitch = (float)LightPitch.Value;
    }

    /// <summary>Format a camera's state for its inspector panel.</summary>
    public static string DescribeCamera(OrbitCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return string.Create(CultureInfo.InvariantCulture, $"yaw {camera.Yaw * 180 / Math.PI:0} deg, pitch {camera.Pitch * 180 / Math.PI:0} deg, distance {camera.Distance:0.00} m");
    }

    /// <summary>A number for the inspector's text boxes.</summary>
    public static string FormatNumber(double value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>Parse the inspector's text box back (an unparsable string keeps <paramref name="fallback"/>).</summary>
    public static double ParseNumber(string text, double fallback)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }
}
