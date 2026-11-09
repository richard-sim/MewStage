// The present-surface contract. The swapchain is backend-specific; this project defines the contract so the GPU
// scheduler can drive acquire, render and present without depending on Vortice or Silk.NET. Headless runs with the
// offscreen swapchain in MewStage.Rendering instead.

using System;

namespace MewStage.Graphics;

/// <summary>Present mode.</summary>
public enum PresentMode : byte
{
    /// <summary>VSync, tear-free, flip-model (the default).</summary>
    Fifo = 0,

    /// <summary>Low-latency: the newest frame wins, no tearing.</summary>
    Mailbox = 1,

    /// <summary>Uncapped (may tear).</summary>
    Immediate = 2,
}

/// <summary>A presentable backbuffer chain. The scheduler acquires the next backbuffer, the render graph imports it as
/// its output, and the scheduler presents at the end of the frame.</summary>
public interface ISwapchain : IDisposable
{
    /// <summary>Backbuffer count.</summary>
    int BackbufferCount { get; }

    /// <summary>Backbuffer format.</summary>
    Format Format { get; }

    /// <summary>Width in pixels.</summary>
    uint Width { get; }

    /// <summary>Height in pixels.</summary>
    uint Height { get; }

    /// <summary>The present mode.</summary>
    PresentMode PresentMode { get; }

    /// <summary>Acquires the next backbuffer to render into.</summary>
    /// <param name="backbufferIndex">The chain index of the acquired backbuffer.</param>
    /// <returns>The backbuffer texture.</returns>
    ITexture AcquireNextBackbuffer(out int backbufferIndex);

    /// <summary>Presents the acquired backbuffer.</summary>
    /// <param name="syncInterval">0 = no vsync, 1 = vsync.</param>
    void Present(int syncInterval);

    /// <summary>Recreates the backbuffers at a new size (window resize or device reset).</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    void Resize(uint width, uint height);
}
