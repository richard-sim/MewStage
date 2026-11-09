// Runtime-detected GPU capabilities and the portable baseline. Detection runs once at device creation; the renderer
// targets the baseline even on hardware that exceeds it, so behavior stays portable, and optional tiers are opt-in.

using System;

namespace MewStage.Graphics;

/// <summary>Runtime-detected GPU capabilities. Every optional tier defaults to its baseline (off) value through
/// <see cref="Baseline"/>; a backend raises individual fields when detection confirms support.</summary>
public readonly struct GpuCapabilities : IEquatable<GpuCapabilities>
{
    /// <summary>How compute is submitted relative to graphics.</summary>
    public ComputeSubmissionMode ComputeSubmission { get; init; }

    /// <summary>Bindless tier.</summary>
    public BindlessTier Bindless { get; init; }

    /// <summary>Indirect drawing tier.</summary>
    public IndirectTier IndirectDraw { get; init; }

    /// <summary>Variable-rate-shading tier.</summary>
    public VrsTier Vrs { get; init; }

    /// <summary>Mesh shaders are available.</summary>
    public bool MeshShaders { get; init; }

    /// <summary>Hardware tessellation is available.</summary>
    public bool HardwareTessellation { get; init; }

    /// <summary>Largest MSAA sample count the device supports for the color and depth formats in use.</summary>
    public int MaxMsaaSamples { get; init; }

    /// <summary>A tile-based deferred GPU (mobile, Apple).</summary>
    public bool TileBasedDeferred { get; init; }

    /// <summary>Unified memory (integrated GPUs).</summary>
    public bool IsUma { get; init; }

    /// <summary>Compute wave / subgroup width in lanes.</summary>
    public int WaveLaneCount { get; init; }

    /// <summary>The portable baseline: compute, structured and UAV buffers, timestamp queries, timeline fences and MSAA 4,
    /// with nothing optional.</summary>
    public static GpuCapabilities Baseline => new()
    {
        ComputeSubmission = ComputeSubmissionMode.SameQueue,
        Bindless = BindlessTier.None,
        IndirectDraw = IndirectTier.None,
        Vrs = VrsTier.None,
        MeshShaders = false,
        HardwareTessellation = false,
        MaxMsaaSamples = 4,
        TileBasedDeferred = false,
        IsUma = false,
        WaveLaneCount = 64,
    };

    /// <summary>True when compute can overlap graphics on a real second queue.</summary>
    public bool HasAsyncCompute => ComputeSubmission == ComputeSubmissionMode.AsyncCompute;

    /// <summary>True when any bindless tier is available.</summary>
    public bool HasBindless => Bindless != BindlessTier.None;

    /// <summary>True when any indirect drawing tier is available.</summary>
    public bool HasIndirectDraw => IndirectDraw != IndirectTier.None;

    /// <summary>True when any variable-rate-shading tier is available.</summary>
    public bool HasVrs => Vrs != VrsTier.None;

    /// <summary>Value equality.</summary>
    public static bool operator ==(GpuCapabilities left, GpuCapabilities right)
    {
        return left.Equals(right);
    }

    /// <summary>Value inequality.</summary>
    public static bool operator !=(GpuCapabilities left, GpuCapabilities right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public bool Equals(GpuCapabilities other)
    {
        return ComputeSubmission == other.ComputeSubmission && Bindless == other.Bindless
            && IndirectDraw == other.IndirectDraw && Vrs == other.Vrs && MeshShaders == other.MeshShaders
            && HardwareTessellation == other.HardwareTessellation && MaxMsaaSamples == other.MaxMsaaSamples
            && TileBasedDeferred == other.TileBasedDeferred && IsUma == other.IsUma && WaveLaneCount == other.WaveLaneCount;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is GpuCapabilities c && Equals(c);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode h = default;
        h.Add((int)ComputeSubmission);
        h.Add((int)Bindless);
        h.Add((int)IndirectDraw);
        h.Add((int)Vrs);
        h.Add(MeshShaders);
        h.Add(HardwareTessellation);
        h.Add(MaxMsaaSamples);
        h.Add(TileBasedDeferred);
        h.Add(IsUma);
        h.Add(WaveLaneCount);
        return h.ToHashCode();
    }
}
