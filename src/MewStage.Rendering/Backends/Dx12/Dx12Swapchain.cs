// Windowed DX12 swapchain — MewStage.Rendering. IDXGISwapChain flip-model
// (FLIP_DISCARD) created for an HWND (the Presentation host owns the window). Waitable, capped to 2-3 queued
// frames; FIFO via Present sync-interval 1, tear/VRR via ALLOW_TEARING + sync 0. Backbuffers are wrapped as
// external D12Textures the render graph imports. RUNTIME is Windows-only (d3d12/dxgi); compile + AOT-clean here,
// runtime-verified on Windows.

using System;

using MewStage.Graphics;
using MewStage.Graphics.Diagnostics;

using Vortice.DXGI;

using D3D = Vortice.Direct3D12;
using DXGI = Vortice.DXGI;
using Format = MewStage.Graphics.Format;

namespace MewStage.Rendering.Backends.Dx12;

/// <summary>A flip-model DXGI swapchain over a Win32 window.</summary>
public sealed class Dx12Swapchain : ISwapchain
{
    private readonly Dx12Device _dev;
    private readonly IntPtr _hwnd;
    private readonly bool _allowTearing;
    private readonly IDXGISwapChain3 _swapchain = null!;
    private D12Texture[] _backbuffers = [];

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

    /// <summary>Creates the swapchain.</summary>
    /// <param name="device">The device.</param>
    /// <param name="hwnd">The window handle.</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="bufferCount">Backbuffer count.</param>
    /// <param name="format">Backbuffer format.</param>
    /// <param name="presentMode">Present mode.</param>
    public Dx12Swapchain(Dx12Device device, IntPtr hwnd, uint width, uint height, int bufferCount = 3,
        Format format = Format.B8G8R8A8_UNorm, PresentMode presentMode = PresentMode.Fifo)
    {
        _dev = device ?? throw new ArgumentNullException(nameof(device));
        _hwnd = hwnd;
        Format = format;
        PresentMode = presentMode;
        Width = width;
        Height = height;
        _allowTearing = presentMode != PresentMode.Fifo;
        using IDXGIFactory4 factory = DXGI.DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
        var desc = new SwapChainDescription1
        {
            Width = width,
            Height = height,
            Format = Dx12Map.Format(format),
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = (uint)Math.Max(2, bufferCount),
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Unspecified,
            Flags = _allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
        };
        using IDXGISwapChain1 sc1 = factory.CreateSwapChainForHwnd(_dev.GraphicsQueueRaw, _hwnd, desc);
        _swapchain = sc1.QueryInterface<IDXGISwapChain3>();
        WrapBuffers();
    }

    private void WrapBuffers()
    {
        int n = BackbufferCountInternal();
        _backbuffers = new D12Texture[n];
        var d = new TextureDesc { Dimension = TextureDimension.Tex2D, Format = Format, Width = Width, Height = Height, DepthOrArrayLayers = 1, MipLevels = 1, SampleCount = 1, Usage = ResourceUsage.RenderTarget };
        for (int i = 0; i < n; i++)
        {
            _backbuffers[i] = new D12Texture(_dev, _swapchain.GetBuffer<D3D.ID3D12Resource>((uint)i), d);
        }
    }

    private int BackbufferCountInternal()
    {
        return (int)_swapchain.Description1.BufferCount;
    }

    /// <inheritdoc />
    public ITexture AcquireNextBackbuffer(out int backbufferIndex)
    {
        backbufferIndex = (int)_swapchain.CurrentBackBufferIndex;
        return _backbuffers[backbufferIndex];
    }

    /// <inheritdoc />
    public void Present(int syncInterval)
    {
        using ProfileScope zone = Profiler.BeginZone("Dx12Swapchain.Present");

        PresentFlags flags = (_allowTearing && syncInterval == 0) ? PresentFlags.AllowTearing : PresentFlags.None;
        _ = _swapchain.Present((uint)syncInterval, flags);
        // Env-gated (MEWSTAGE_D3D12_DEBUG=1): drain + log D3D12 debug/validation messages each frame so a GBV
        // message naming a bad draw is printed near the offending Present. No-op when debug is disabled.
        if (Dx12Device.DebugEnabled)
        {
            _dev.DrainInfoQueue("present");
        }
    }

    /// <inheritdoc />
    public void Resize(uint width, uint height)
    {
        using ProfileScope zone = Profiler.BeginZone("Dx12Swapchain.Resize");

        foreach (D12Texture t in _backbuffers)
        {
            t.Dispose();
        }

        Width = width;
        Height = height;
        _ = _swapchain.ResizeBuffers((uint)_backbuffers.Length, width, height, Dx12Map.Format(Format),
            _allowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None);
        WrapBuffers();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (D12Texture t in _backbuffers)
        {
            t.Dispose();
        }

        _swapchain?.Dispose();
    }
}
