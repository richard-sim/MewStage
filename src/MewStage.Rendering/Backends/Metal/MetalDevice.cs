// The Metal device stub. It implements the device contract behind the interface and throws NotSupportedException
// until a Metal backend exists, so backend selection can construct and detect it (IsSupported = false) and fall back,
// and the scheduler compiles identically on every platform. On macOS the Vulkan backend runs through MoltenVK.

using System;
using System.Runtime.CompilerServices;

using MewStage.Graphics;

namespace MewStage.Rendering.Backends.Metal;

/// <summary>A placeholder <see cref="IGraphicsDevice"/> for Metal.</summary>
public sealed class MetalDevice : IGraphicsDevice
{
    /// <summary>Always false until a Metal backend is implemented, so a selector can skip it cleanly.</summary>
    public static bool IsSupported => false;

    /// <inheritdoc />
    public event Action? OnDeviceLost
    {
        add
        {
        }

        remove
        {
        }
    }

    /// <inheritdoc />
    public GpuCapabilities Caps => GpuCapabilities.Baseline;

    /// <inheritdoc />
    public bool IsUma => true;   // Apple silicon is unified-memory

    /// <inheritdoc />
    public ICommandQueue GraphicsQueue => throw NotSupported();

    /// <inheritdoc />
    public ICommandQueue ComputeQueue => throw NotSupported();

    /// <inheritdoc />
    public ICommandQueue CopyQueue => throw NotSupported();

    /// <inheritdoc />
    public IBuffer CreateBuffer(in BufferDesc desc)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public ITexture CreateTexture(in TextureDesc desc)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public IBufferView CreateView(IBuffer buffer, in BufferViewDesc desc)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public ITextureView CreateView(ITexture texture, in TextureViewDesc desc)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public IDescriptorHeap CreateDescriptorHeap(DescriptorKind kind, int capacity, bool shaderVisible)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public IPipelineLayout CreatePipelineLayout(in PipelineLayoutDesc desc)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public ISampler CreateSampler(in SamplerDesc desc)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public IComputeRecorder CreateComputeRecorder(ICommandList list)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public IFence CreateFence(ulong initialValue)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public IQueryHeap CreateTimestampQueryHeap(int capacity)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public ICommandSignature? CreateCommandSignature(in CommandSignatureDesc desc)
    {
        return null;
    }

    /// <inheritdoc />
    public IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public IRenderPipeline CreateRenderPipeline(in RenderPipelineDesc desc)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public void DeferDestroy(IGpuResource resource)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public void RetireDeferred(long currentFrame, int framesInFlight)
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public int FlushDeferred()
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public DeviceStatus PollStatus()
    {
        return DeviceStatus.Ok;
    }

    /// <summary>A best-effort no-op: naming must never throw.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="name">The name.</param>
    public void SetDebugName(IGpuResource resource, string name)
    {
    }

    /// <inheritdoc />
    public bool TryRecreate()
    {
        throw NotSupported();
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static NotSupportedException NotSupported([CallerMemberName] string member = "")
    {
        return new NotSupportedException($"Metal backend not implemented ('{member}'). Use Direct3D 12 (Windows) or Vulkan (Linux, or MoltenVK on macOS).");
    }
}
