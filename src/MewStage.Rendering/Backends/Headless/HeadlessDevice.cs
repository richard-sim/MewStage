// Headless (CPU/no-GPU) IGraphicsDevice backend — MewStage.Rendering.
//
// A complete, fully-managed realization of the device contract with NO GPU dependency. It is the backend
// the deterministic/headless paths use and the substrate every
// scheduler / frame-context / render-graph-executor / compute-recorder unit test runs against. Resources are
// real CPU allocations (host-visible classes are genuinely mapped via NativeMemory so the upload ring works
// in tests); the "GPU" is modeled as always-caught-up: a queue Submit applies its fence signals immediately,
// so FrameContext fence waits never block. This gives end-to-end coverage of the submission/sync/retirement
// machinery without a device. Real GPU correctness is covered by the Vulkan backend under lavapipe and the
// D3D12 backend on Windows.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

using MewStage.Graphics;

namespace MewStage.Rendering.Backends.Headless;

internal abstract class HeadlessResource : IGpuResource
{
    private static long s_next;
    public ulong Handle { get; }
    public bool Disposed { get; private set; }
    protected HeadlessResource()
    {
        Handle = (ulong)Interlocked.Increment(ref s_next);
    }

    public virtual void Dispose()
    {
        Disposed = true;
    }
}

internal sealed unsafe class HeadlessBuffer : HeadlessResource, IBuffer, IMappedBuffer
{
    private void* _mapped;
    public ulong SizeBytes { get; }
    public MemoryClass Memory { get; }
    public bool IsHostVisible => Memory is MemoryClass.Upload or MemoryClass.Readback or MemoryClass.Shared;
    public IntPtr MappedPointer => (IntPtr)_mapped;

    public HeadlessBuffer(in BufferDesc d)
    {
        SizeBytes = d.SizeBytes;
        Memory = d.Memory;
        if (IsHostVisible && d.SizeBytes > 0)
        {
            _mapped = NativeMemory.AllocZeroed((nuint)d.SizeBytes);
        }
    }

    public override void Dispose()
    {
        if (_mapped != null)
        {
            NativeMemory.Free(_mapped);
            _mapped = null;
        }

        base.Dispose();
    }
}

internal sealed class HeadlessTexture : HeadlessResource, ITexture
{
    public TextureDesc Desc { get; }
    public HeadlessTexture(in TextureDesc d)
    {
        Desc = d;
    }
}

internal sealed class HeadlessBufferView : HeadlessResource, IBufferView
{
    public GpuBufferView View { get; }
    public HeadlessBufferView(IBuffer b, in BufferViewDesc d)
    {
        View = new GpuBufferView(b.Handle, d.Offset, d.SizeBytes == 0 ? b.SizeBytes : d.SizeBytes);
    }
}

internal sealed class HeadlessTextureView : HeadlessResource, ITextureView { }
internal sealed class HeadlessSampler : HeadlessResource, ISampler { }
internal sealed class HeadlessPipelineLayout : HeadlessResource, IPipelineLayout { }
internal sealed class HeadlessComputePipeline : HeadlessResource, IComputePipeline { public string Name { get; init; } = ""; }
internal sealed class HeadlessRenderPipeline : HeadlessResource, IRenderPipeline { public string Name { get; init; } = ""; }

internal sealed class HeadlessFence : HeadlessResource, IFence
{
    private long _value;
    public HeadlessFence(ulong initial)
    {
        _value = (long)initial;
    }

    public ulong CompletedValue => (ulong)Interlocked.Read(ref _value);
    /// <summary>Headless GPU is always caught up — a wait never blocks (the value is signaled at Submit time).</summary>
    public void Wait(ulong value) { /* no-op: signals are applied synchronously at Submit */ }
    internal ulong SignalNext()
    {
        return (ulong)Interlocked.Increment(ref _value);
    }

    internal void SignalTo(ulong value)
    {
        long v = (long)value;
        long cur;
        do
        {
            cur = Interlocked.Read(ref _value);
            if (v <= cur)
            {
                return;
            }
        } while (Interlocked.CompareExchange(ref _value, v, cur) != cur);
    }
}

internal sealed class HeadlessQueryHeap : HeadlessResource, IQueryHeap
{
    public int RangeStart => 0;
    public int Capacity { get; }
    public HeadlessQueryHeap(int capacity)
    {
        Capacity = capacity;
    }

    public void ResolveTo(IBuffer dst) { /* no timings on the headless backend */ }
}

internal sealed class HeadlessDescriptorHeap : HeadlessResource, IDescriptorHeap
{
    private int _used;
    public DescriptorKind Kind { get; }
    public int Capacity { get; }
    public HeadlessDescriptorHeap(DescriptorKind kind, int capacity)
    {
        Kind = kind;
        Capacity = capacity;
    }
    public DescriptorRange Allocate(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        int start = Interlocked.Add(ref _used, count) - count;
        return start + count > Capacity
            ? throw new InvalidOperationException("Headless descriptor heap exhausted.")
            : new DescriptorRange(Handle, start, count);
    }
    internal void Rewind()
    {
        Interlocked.Exchange(ref _used, 0);
    }
}

/// <summary>Recording command list — captures barriers + timestamp writes in order for headless inspection/tests.</summary>
internal sealed class HeadlessCommandList : ICommandList
{
    public QueueKind Queue { get; }
    public bool Open { get; private set; }
    public readonly List<ResourceBarrierDesc> Barriers = [];
    public readonly List<(ulong heap, int index)> Timestamps = [];
    public HeadlessCommandList(QueueKind queue)
    {
        Queue = queue;
    }

    public void Begin()
    {
        Open = true;
        Barriers.Clear();
        Timestamps.Clear();
    }
    public void Close()
    {
        Open = false;
    }

    public void ResourceBarrier(ReadOnlySpan<ResourceBarrierDesc> barriers)
    {
        foreach (ResourceBarrierDesc b in barriers)
        {
            Barriers.Add(b);
        }
    }
    public void WriteTimestamp(IQueryHeap heap, int index)
    {
        Timestamps.Add((heap.Handle, index));
    }

    public void CopyBufferToTexture(IBuffer src, ITexture dst, uint width, uint height) { /* headless: no GPU copy */ }
    public void CopyBufferToTextureRegion(IBuffer src, ITexture dst, uint dstX, uint dstY, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel) { /* headless: no GPU copy */ }
    public void CopyTextureToBuffer(ITexture src, IBuffer dst, uint width, uint height) { /* headless: no GPU copy */ }
    public void CopyBufferToTextureMip(IBuffer src, ulong srcOffsetBytes, ITexture dst, uint mip, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel) { /* headless: no GPU copy */ }
    public void CopyBuffer(IBuffer src, ulong srcOffsetBytes, IBuffer dst, ulong dstOffsetBytes, ulong sizeBytes) { /* headless: no GPU copy */ }

    public void BeginEvent(string name, uint color) { /* headless: no GPU markers */ }
    public void EndEvent() { /* headless: no GPU markers */ }
    public void SetMarker(string name, uint color) { /* headless: no GPU markers */ }
    public void Dispose() { }
}

internal sealed class HeadlessQueue : ICommandQueue
{
    public QueueKind Kind { get; }
    public HeadlessQueue(QueueKind kind)
    {
        Kind = kind;
    }

    public ICommandList AcquireCommandList()
    {
        return new HeadlessCommandList(Kind);
    }

    public void Submit(ReadOnlySpan<ICommandList> lists, ReadOnlySpan<FenceWait> waitOn, ReadOnlySpan<FenceSignal> signal)
    {
        // Synchronous "GPU": waits are already satisfied (caught up); apply signals immediately.
        foreach (FenceSignal s in signal)
        {
            if (s.Fence is HeadlessFence f)
            {
                f.SignalTo(s.Value);
            }
        }
    }

    public ulong Signal(IFence fence)
    {
        return fence is HeadlessFence f
            ? f.SignalNext()
            : throw new ArgumentException("Foreign fence on the headless queue.", nameof(fence));
    }

    /// <summary>No-op: the headless "GPU" is synchronous (submitted work is already complete), so the queue is
    /// always idle.</summary>
    public void WaitIdle() { }

    public void BeginEvent(string name, uint color) { /* headless: no GPU markers */ }
    public void EndEvent() { /* headless: no GPU markers */ }
    public void SetMarker(string name, uint color) { /* headless: no GPU markers */ }
}

/// <summary>
/// The headless device. Reports <see cref="GpuCapabilities.Baseline"/>, exposes one logical
/// queue object per kind (all backed by the same synchronous model), and creates real CPU resources. Never
/// loses the device.
/// </summary>
public sealed class HeadlessDevice : IGraphicsDevice
{
    private readonly DeferredDestroyQueue _deferred = new();   // the shared fence-keyed ring
    private long _retireFrame;
    private readonly Lock _gate = new();

    /// <inheritdoc />
    public GpuCapabilities Caps { get; }
    /// <inheritdoc />
    public bool IsUma => Caps.IsUma;
    /// <inheritdoc />
    public ICommandQueue GraphicsQueue { get; }
    /// <inheritdoc />
    public ICommandQueue ComputeQueue { get; }
    /// <inheritdoc />
    public ICommandQueue CopyQueue { get; }

    /// <summary>Creates the device.</summary>
    /// <param name="caps">The capabilities to report; <see cref="GpuCapabilities.Baseline"/> when null.</param>
    public HeadlessDevice(GpuCapabilities? caps = null)
    {
        Caps = caps ?? GpuCapabilities.Baseline;
        GraphicsQueue = new HeadlessQueue(QueueKind.Graphics);
        // Baseline = single-queue: compute/copy alias graphics semantics (still distinct objects,).
        ComputeQueue = Caps.HasAsyncCompute ? new HeadlessQueue(QueueKind.AsyncCompute) : GraphicsQueue;
        CopyQueue = new HeadlessQueue(QueueKind.Copy);
    }

    /// <inheritdoc />
    public IBuffer CreateBuffer(in BufferDesc desc)
    {
        return new HeadlessBuffer(desc);
    }

    /// <inheritdoc />
    public ITexture CreateTexture(in TextureDesc desc)
    {
        return new HeadlessTexture(desc);
    }

    /// <inheritdoc />
    public IBufferView CreateView(IBuffer buffer, in BufferViewDesc desc)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return new HeadlessBufferView(buffer, desc);
    }
    /// <inheritdoc />
    public ITextureView CreateView(ITexture texture, in TextureViewDesc desc)
    {
        ArgumentNullException.ThrowIfNull(texture);
        return new HeadlessTextureView();
    }
    /// <inheritdoc />
    public IDescriptorHeap CreateDescriptorHeap(DescriptorKind kind, int capacity, bool shaderVisible)
    {
        return new HeadlessDescriptorHeap(kind, capacity);
    }

    /// <inheritdoc />
    public IPipelineLayout CreatePipelineLayout(in PipelineLayoutDesc desc)
    {
        return new HeadlessPipelineLayout();
    }

    /// <inheritdoc />
    public ISampler CreateSampler(in SamplerDesc desc)
    {
        return new HeadlessSampler();
    }

    /// <inheritdoc />
    public IComputeRecorder CreateComputeRecorder(ICommandList list)
    {
        return new Compute.RecordingComputeRecorder();
    }

    /// <inheritdoc />
    public IFence CreateFence(ulong initialValue)
    {
        return new HeadlessFence(initialValue);
    }

    /// <inheritdoc />
    public IQueryHeap CreateTimestampQueryHeap(int capacity)
    {
        return new HeadlessQueryHeap(capacity);
    }

    /// <inheritdoc />
    public ICommandSignature? CreateCommandSignature(in CommandSignatureDesc desc)
    {
        return null; // baseline: no indirect
    }

    /// <inheritdoc />
    public IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
    {
        return new HeadlessComputePipeline { Name = desc.Name };
    }

    /// <inheritdoc />
    public IRenderPipeline CreateRenderPipeline(in RenderPipelineDesc desc)
    {
        return new HeadlessRenderPipeline { Name = desc.Name };
    }

    /// <inheritdoc />
    public void DeferDestroy(IGpuResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_gate)
        {
            _deferred.Add(resource, _retireFrame);
        }
    }

    /// <summary>Advance the retire frame and dispose resources deferred at least framesInFlight frames ago.</summary>
    public void RetireDeferred(long currentFrame, int framesInFlight)
    {
        lock (_gate)
        {
            _retireFrame = currentFrame;
            _ = _deferred.Retire(currentFrame, framesInFlight);
        }
    }

    /// <summary>Dispose everything queued for deferred destruction (teardown; caller drained the GPU first).</summary>
    public int FlushDeferred()
    {
        lock (_gate)
        {
            return _deferred.Flush();
        }
    }

    /// <inheritdoc />
    public int PendingDeferredDestroys
    {
        get
        {
            lock (_gate)
            {
                return _deferred.Count;
            }
        }
    }

    /// <inheritdoc />
    public DeviceStatus PollStatus()
    {
        return DeviceStatus.Ok;
    }

    /// <inheritdoc />
    public void SetDebugName(IGpuResource resource, string name) { /* headless: no capture backend */ }
    /// <inheritdoc />
    public event Action? OnDeviceLost { add { } remove { } }
    /// <inheritdoc />
    public bool TryRecreate()
    {
        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        FlushDeferred();
    }
}
