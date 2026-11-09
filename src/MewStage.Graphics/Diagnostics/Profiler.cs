// The profiler seam. MewStage ships no profiler binding; every zone compiles to a no-op (an empty struct) and the
// call sites in the scheduler and the render graph stay, so an application built on this stack can plug one in
// (Tracy, Superluminal, PIX) by filling in these two types.

using System;
using System.Runtime.CompilerServices;

namespace MewStage.Graphics.Diagnostics;

/// <summary>A CPU profiling zone: <c>using var zone = Profiler.BeginZone("Name");</c> opens a zone that closes when the
/// scope exits. Empty here; a profiler binding would carry its context in it.</summary>
public readonly struct ProfileScope : IDisposable
{
    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
    }
}

/// <summary>Facade over an optional CPU profiler and the GPU marker gate. Every method is a no-op here.</summary>
public static class Profiler
{
    private static volatile bool s_enabled = true;
    private static volatile bool s_gpuMarkers = true;

    /// <summary>Runtime switch for every zone (default true).</summary>
    public static bool Enabled
    {
        get => s_enabled;
        set => s_enabled = value;
    }

    /// <summary>Runtime gate for GPU command-list and queue markers (D3D12 events, Vulkan debug-utils labels). The backend
    /// marker seam checks it so GPU markers can be toggled independently of CPU zones. Implies <see cref="Enabled"/>.</summary>
    public static bool GpuMarkers
    {
        get => s_gpuMarkers && s_enabled;
        set => s_gpuMarkers = value;
    }

    /// <summary>Opens a named CPU profiling zone. Optional <paramref name="color"/> is ARGB (<see cref="EventColor"/>);
    /// 0 = automatic.</summary>
    /// <param name="name">Zone name.</param>
    /// <param name="color">ARGB color.</param>
    /// <returns>A scope that closes the zone when disposed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ProfileScope BeginZone(string name, uint color = 0)
    {
        _ = name;
        _ = color;
        return default;
    }

    /// <summary>Marks the end of the main frame (a frame-time histogram in a profiler).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FrameMark()
    {
    }
}
