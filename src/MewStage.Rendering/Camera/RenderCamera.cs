// The render camera: builds the world-to-view matrix (right-handed look-to; the camera looks down its -Z) and composes
// it with the reversed-Z projection. ROW-VECTOR convention (v' = v * M), composed left to right (world * View * Proj),
// which is System.Numerics' native layout; matrices upload to the GPU as a plain memcpy and shaders do mul(v, M).
// Pure and headless-testable.

using System.Numerics;

namespace MewStage.Rendering.Camera;

/// <summary>A perspective or orthographic camera in the world frame (+X right, +Y forward, +Z up).</summary>
public sealed class RenderCamera
{
    /// <summary>World position.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Unit world forward (default +Y).</summary>
    public Vector3 Forward { get; set; } = new(0, 1, 0);

    /// <summary>World up reference (default +Z).</summary>
    public Vector3 Up { get; set; } = new(0, 0, 1);

    /// <summary>Vertical field of view in radians (default about 57 degrees).</summary>
    public float FovYRadians { get; set; } = 1.0f;

    /// <summary>Near plane distance.</summary>
    public float Near { get; set; } = 0.05f;

    /// <summary>Far plane distance.</summary>
    public float Far { get; set; } = 200f;

    /// <summary>The clip convention of the backend in use.</summary>
    public ClipConvention Clip { get; set; } = ClipConvention.D3DMetal;

    /// <summary>Orthographic (parallel) projection instead of perspective. The view volume is <see cref="OrthoHeight"/>
    /// tall, OrthoHeight * aspect wide, and spans [Near, Far] in front of <see cref="Position"/>.</summary>
    public bool Orthographic { get; set; }

    /// <summary>Full vertical extent of the orthographic view volume, in world units.</summary>
    public float OrthoHeight { get; set; } = 1f;

    /// <summary>The world-to-view matrix (right-handed look-to; the camera looks down -Z_view). Translation in row 4.</summary>
    /// <returns>The view matrix.</returns>
    public Matrix4x4 View()
    {
        Vector3 pos = Position;
        Vector3 f = Norm(Forward);
        Vector3 upRef = Norm(Up);
        Vector3 right = Norm(Vector3.Cross(f, upRef));   // +X_view
        var trueUp = Vector3.Cross(right, f);        // +Y_view

        // Row-vector: the COLUMNS are the view axes in world; -f is +Z_view (the camera looks down -Z). Translation in
        // row 4 is -dot(axis, pos). This matches System.Numerics CreateLookAt's layout.
        return new Matrix4x4(
            right.X, trueUp.X, -f.X, 0,
            right.Y, trueUp.Y, -f.Y, 0,
            right.Z, trueUp.Z, -f.Z, 0,
            -Vector3.Dot(right, pos), -Vector3.Dot(trueUp, pos), Vector3.Dot(f, pos), 1);
    }

    /// <summary>The projection matrix for an aspect ratio.</summary>
    /// <param name="aspect">Width / height.</param>
    /// <returns>The projection matrix.</returns>
    public Matrix4x4 Projection(float aspect)
    {
        return Orthographic
            ? ReversedZProjection.Orthographic(OrthoHeight * aspect, OrthoHeight, Near, Far, Clip)
            : ReversedZProjection.Perspective(FovYRadians, aspect, Near, Far, Clip);
    }

    /// <summary>The view-projection matrix (row-vector compose: View * Projection). Left-multiply by an object's world matrix.</summary>
    /// <param name="aspect">Width / height.</param>
    /// <returns>The view-projection matrix.</returns>
    public Matrix4x4 ViewProjection(float aspect)
    {
        return View() * Projection(aspect);
    }

    private static Vector3 Norm(Vector3 v)
    {
        float len = v.Length();
        return len > 1e-12f ? v / len : new Vector3(0, 1, 0);
    }
}
