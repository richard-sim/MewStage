// A device-agnostic ISwapchain whose "backbuffers" are device textures the renderer draws into; Present is a no-op or
// a readback hook. This is the headless / CI / lavapipe present path (no window surface needed).

using System;
using System.Globalization;

using MewStage.Graphics;

namespace MewStage.Rendering.Backends;

/// <summary>A swapchain over plain device textures, for headless rendering and tests.</summary>
public sealed class OffscreenSwapchain : ISwapchain
{
    private readonly IGraphicsDevice _device;
    private ITexture[] _backbuffers;
    private int _index;

    /// <summary>Creates the chain.</summary>
    /// <param name="device">The device.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="bufferCount">Backbuffer count (at least 2).</param>
    /// <param name="format">Backbuffer format.</param>
    /// <param name="presentMode">The nominal present mode (reported, never acted on).</param>
    public OffscreenSwapchain(IGraphicsDevice device, uint width, uint height, int bufferCount = 2, Format format = Format.B8G8R8A8_UNorm, PresentMode presentMode = PresentMode.Fifo)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        Format = format;
        PresentMode = presentMode;
        Width = width;
        Height = height;
        _backbuffers = Allocate(Math.Max(2, bufferCount));
    }

    /// <inheritdoc />
    public int BackbufferCount => _backbuffers.Length;

    /// <inheritdoc />
    public Format Format { get; }

    /// <inheritdoc />
    public uint Width { get; private set; }

    /// <inheritdoc />
    public uint Height { get; private set; }

    /// <inheritdoc />
    public PresentMode PresentMode { get; }

    /// <summary>Called after <see cref="Present"/> with the just-presented backbuffer (a readback hook for tests).</summary>
    public Action<ITexture>? OnPresent { get; set; }

    /// <inheritdoc />
    public ITexture AcquireNextBackbuffer(out int backbufferIndex)
    {
        backbufferIndex = _index;
        return _backbuffers[_index];
    }

    /// <inheritdoc />
    public void Present(int syncInterval)
    {
        OnPresent?.Invoke(_backbuffers[_index]);
        _index = (_index + 1) % _backbuffers.Length;
    }

    /// <inheritdoc />
    public void Resize(uint width, uint height)
    {
        foreach (ITexture t in _backbuffers)
        {
            t.Dispose();
        }

        Width = width;
        Height = height;
        _index = 0;
        _backbuffers = Allocate(_backbuffers.Length);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (ITexture t in _backbuffers)
        {
            t.Dispose();
        }
    }

    private ITexture[] Allocate(int n)
    {
        var bb = new ITexture[n];
        for (int i = 0; i < n; i++)
        {
            bb[i] = _device.CreateTexture(new TextureDesc
            {
                Dimension = TextureDimension.Tex2D,
                Format = Format,
                Width = Width,
                Height = Height,
                DepthOrArrayLayers = 1,
                MipLevels = 1,
                SampleCount = 1,
                Usage = ResourceUsage.RenderTarget | ResourceUsage.CopySource,
                DebugName = "backbuffer" + i.ToString(CultureInfo.InvariantCulture),
            });
        }

        return bb;
    }
}
