using System;
using System.Numerics;
using System.Runtime.InteropServices;

using MewStage.Graphics;

namespace MewStage.Scene;

/// <summary>One cube vertex as <c>cube.hlsl</c> reads it: position, normal, color (40 bytes).</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct CubeVertex
{
    /// <summary>Creates a vertex.</summary>
    public CubeVertex(Vector3 position, Vector3 normal, Vector4 color)
    {
        Position = position;
        Normal = normal;
        Color = color;
    }

    /// <summary>Model-space position.</summary>
    public Vector3 Position { get; }

    /// <summary>Model-space unit normal (flat per face).</summary>
    public Vector3 Normal { get; }

    /// <summary>Straight RGBA color, 0..1.</summary>
    public Vector4 Color { get; }

    /// <summary>The vertex layout for <see cref="RenderPipelineDesc.VertexAttributes"/>.</summary>
    public static VertexAttribute[] Attributes()
    {
        return
        [
            new VertexAttribute { Location = 0, Semantic = "POSITION", Format = Format.R32G32B32_Float, Offset = 0 },
            new VertexAttribute { Location = 1, Semantic = "NORMAL", Format = Format.R32G32B32_Float, Offset = 12 },
            new VertexAttribute { Location = 2, Semantic = "COLOR", Format = Format.R32G32B32A32_Float, Offset = 24 },
        ];
    }

    /// <summary>The vertex stride in bytes.</summary>
    public const int StrideBytes = 40;

    /// <summary>The vertex stride in bytes (as the pipeline desc wants it).</summary>
    public static int Stride => StrideBytes;
}

/// <summary>
/// A unit cube (edge 1, centered on the origin) with flat-shaded faces: 24 vertices (4 per face, each with the face's
/// normal and color) and 36 indices, counter-clockwise front faces when seen from outside.
/// </summary>
public static class CubeMesh
{
    /// <summary>Vertices per cube.</summary>
    public const int VertexCount = 24;

    /// <summary>Indices per cube (12 triangles).</summary>
    public const int IndexCount = 36;

    private static readonly Vector4[] FaceColors =
    [
        new(0.92f, 0.33f, 0.30f, 1f),   // +X red
        new(0.25f, 0.70f, 0.42f, 1f),   // -X green
        new(0.30f, 0.52f, 0.92f, 1f),   // +Y blue
        new(0.95f, 0.78f, 0.25f, 1f),   // -Y yellow
        new(0.90f, 0.90f, 0.92f, 1f),   // +Z white (top)
        new(0.55f, 0.35f, 0.75f, 1f),   // -Z purple (bottom)
    ];

    /// <summary>Fills <paramref name="vertices"/> (24) and <paramref name="indices"/> (36).</summary>
    public static void Build(Span<CubeVertex> vertices, Span<ushort> indices)
    {
        if (vertices.Length < VertexCount || indices.Length < IndexCount)
        {
            throw new ArgumentException("buffers too small for a cube");
        }

        // Each face: normal, then two tangent axes so the corners come out counter-clockwise seen from outside.
        Span<(Vector3 N, Vector3 U, Vector3 V)> faces =
        [
            (new(1, 0, 0), new(0, 1, 0), new(0, 0, 1)),
            (new(-1, 0, 0), new(0, 0, 1), new(0, 1, 0)),
            (new(0, 1, 0), new(0, 0, 1), new(1, 0, 0)),
            (new(0, -1, 0), new(1, 0, 0), new(0, 0, 1)),
            (new(0, 0, 1), new(1, 0, 0), new(0, 1, 0)),
            (new(0, 0, -1), new(0, 1, 0), new(1, 0, 0)),
        ];

        int v = 0;
        int i = 0;
        for (int f = 0; f < 6; f++)
        {
            (Vector3 n, Vector3 u, Vector3 w) = faces[f];
            Vector3 c = n * 0.5f;
            Vector4 color = FaceColors[f];
            vertices[v + 0] = new CubeVertex(c - (u * 0.5f) - (w * 0.5f), n, color);
            vertices[v + 1] = new CubeVertex(c + (u * 0.5f) - (w * 0.5f), n, color);
            vertices[v + 2] = new CubeVertex(c + (u * 0.5f) + (w * 0.5f), n, color);
            vertices[v + 3] = new CubeVertex(c - (u * 0.5f) + (w * 0.5f), n, color);
            indices[i + 0] = (ushort)(v + 0);
            indices[i + 1] = (ushort)(v + 1);
            indices[i + 2] = (ushort)(v + 2);
            indices[i + 3] = (ushort)(v + 0);
            indices[i + 4] = (ushort)(v + 2);
            indices[i + 5] = (ushort)(v + 3);
            v += 4;
            i += 6;
        }
    }
}
