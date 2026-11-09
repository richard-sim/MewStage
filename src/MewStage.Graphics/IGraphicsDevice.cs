// The device contract and the explicit GPU object model. Not a lowest-common-denominator abstraction: it exposes
// modern explicit concepts directly (queues, descriptor heaps, pipeline state objects, explicit resource states
// and barriers, timestamp queries, timeline fences). Feature code never writes barriers by hand; the render graph
// is the only component that synthesizes them. The concrete backends live in MewStage.Rendering.

using System;

namespace MewStage.Graphics;

/// <summary>Base for any GPU resource that participates in deferred destruction.</summary>
public interface IGpuResource : IDisposable
{
    /// <summary>Backend-opaque stable id used by <see cref="GpuBufferView"/> and barrier descriptions.</summary>
    ulong Handle { get; }
}

/// <summary>A GPU buffer.</summary>
public interface IBuffer : IGpuResource
{
    /// <summary>Size in bytes.</summary>
    ulong SizeBytes { get; }

    /// <summary>Memory residency class.</summary>
    MemoryClass Memory { get; }
}

/// <summary>A GPU texture.</summary>
public interface ITexture : IGpuResource
{
    /// <summary>The creation parameters.</summary>
    TextureDesc Desc { get; }
}

/// <summary>A view over a buffer range.</summary>
public interface IBufferView : IGpuResource
{
    /// <summary>The viewed range as a value handle.</summary>
    GpuBufferView View { get; }
}

/// <summary>A view over a texture subresource range.</summary>
public interface ITextureView : IGpuResource
{
}

/// <summary>A root signature (D3D12) or pipeline layout (Vulkan).</summary>
public interface IPipelineLayout : IGpuResource
{
}

/// <summary>A compute pipeline state object.</summary>
public interface IComputePipeline : IGpuResource
{
    /// <summary>Debug name.</summary>
    string Name { get; }
}

/// <summary>A graphics pipeline state object.</summary>
public interface IRenderPipeline : IGpuResource
{
    /// <summary>Debug name.</summary>
    string Name { get; }
}

/// <summary>An indirect command signature; null on the baseline, gated by <see cref="GpuCapabilities.IndirectDraw"/>.</summary>
public interface ICommandSignature : IGpuResource
{
}

/// <summary>An immutable sampler, backed by a device-side cache keyed on <see cref="SamplerDesc"/>.</summary>
public interface ISampler : IGpuResource
{
}

/// <summary>A contiguous descriptor allocation in a shader-visible heap.</summary>
public readonly struct DescriptorRange
{
    /// <summary>Creates a range.</summary>
    /// <param name="heapHandle">The heap's backend handle.</param>
    /// <param name="start">First descriptor index.</param>
    /// <param name="count">Descriptor count.</param>
    public DescriptorRange(ulong heapHandle, int start, int count)
    {
        HeapHandle = heapHandle;
        Start = start;
        Count = count;
    }

    /// <summary>The heap's backend handle.</summary>
    public ulong HeapHandle { get; }

    /// <summary>First descriptor index.</summary>
    public int Start { get; }

    /// <summary>Descriptor count.</summary>
    public int Count { get; }
}

/// <summary>A shader-visible descriptor heap; sub-ranges are sliced per frame context.</summary>
public interface IDescriptorHeap : IGpuResource
{
    /// <summary>The heap class.</summary>
    DescriptorKind Kind { get; }

    /// <summary>Allocates a contiguous range.</summary>
    /// <param name="count">Descriptor count.</param>
    /// <returns>The range.</returns>
    DescriptorRange Allocate(int count);
}

/// <summary>A monotonic 64-bit timeline fence (D3D12 ID3D12Fence; Vulkan timeline semaphore).</summary>
public interface IFence : IGpuResource
{
    /// <summary>The last value the GPU has signalled.</summary>
    ulong CompletedValue { get; }

    /// <summary>Blocks the calling thread until the fence reaches <paramref name="value"/>.</summary>
    /// <param name="value">The timeline value to wait for.</param>
    void Wait(ulong value);
}

/// <summary>A timestamp query heap that resolves into a buffer for deferred readback.</summary>
public interface IQueryHeap : IGpuResource
{
    /// <summary>First query index this heap owns.</summary>
    int RangeStart { get; }

    /// <summary>Query capacity.</summary>
    int Capacity { get; }

    /// <summary>Resolves every query into <paramref name="dst"/>.</summary>
    /// <param name="dst">A readback buffer.</param>
    void ResolveTo(IBuffer dst);
}

/// <summary>The recording surface for one submission. The render graph and the compute recorder drive it; feature
/// code does not. Barriers are issued only by the graph compiler.</summary>
public interface ICommandList : IDisposable
{
    /// <summary>The queue class this list records for.</summary>
    QueueKind Queue { get; }

    /// <summary>Starts recording.</summary>
    void Begin();

    /// <summary>Ends recording.</summary>
    void Close();

    /// <summary>Records resource barriers.</summary>
    /// <param name="barriers">The barriers.</param>
    void ResourceBarrier(ReadOnlySpan<ResourceBarrierDesc> barriers);

    /// <summary>Writes a timestamp into <paramref name="heap"/> at <paramref name="index"/>.</summary>
    /// <param name="heap">The query heap.</param>
    /// <param name="index">Query index.</param>
    void WriteTimestamp(IQueryHeap heap, int index);

    /// <summary>Copies a host-visible buffer's tightly packed RGBA8 rows into texture subresource 0 (a one-shot upload).
    /// The destination must be in <see cref="ResourceState.CopyDest"/>.</summary>
    /// <param name="src">The source buffer.</param>
    /// <param name="dst">The destination texture.</param>
    /// <param name="width">Width in texels.</param>
    /// <param name="height">Height in texels.</param>
    void CopyBufferToTexture(IBuffer src, ITexture dst, uint width, uint height);

    /// <summary>Copies texture subresource 0 into a readback buffer. D3D12 writes 256-byte-aligned rows
    /// (rowPitch = align(width * bpp, 256)); Vulkan and the headless device write tight rows, so the caller
    /// de-aligns per backend.</summary>
    /// <param name="src">The source texture.</param>
    /// <param name="dst">The destination buffer.</param>
    /// <param name="width">Width in texels.</param>
    /// <param name="height">Height in texels.</param>
    void CopyTextureToBuffer(ITexture src, IBuffer dst, uint width, uint height);

    /// <summary>Copies a region of a host-visible buffer into texture subresource 0 at (<paramref name="dstX"/>,
    /// <paramref name="dstY"/>). The buffer is laid out with <paramref name="srcRowPitchBytes"/> per row (D3D12 requires
    /// 256-byte alignment); <paramref name="bytesPerPixel"/> selects the texel size (RGBA8 = 4, R8 = 1). The destination
    /// must be in <see cref="ResourceState.CopyDest"/>.</summary>
    /// <param name="src">The source buffer.</param>
    /// <param name="dst">The destination texture.</param>
    /// <param name="dstX">Destination x in texels.</param>
    /// <param name="dstY">Destination y in texels.</param>
    /// <param name="width">Width in texels.</param>
    /// <param name="height">Height in texels.</param>
    /// <param name="srcRowPitchBytes">Source row pitch in bytes.</param>
    /// <param name="bytesPerPixel">Bytes per texel.</param>
    void CopyBufferToTextureRegion(IBuffer src, ITexture dst, uint dstX, uint dstY, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel);

    /// <summary>Copies <paramref name="width"/> x <paramref name="height"/> texels from a host-visible buffer (rows of
    /// <paramref name="srcRowPitchBytes"/>, D3D12 256-byte aligned, starting at <paramref name="srcOffsetBytes"/>, D3D12
    /// 512-byte aligned) into mip level <paramref name="mip"/> of <paramref name="dst"/>. The destination must be in
    /// <see cref="ResourceState.CopyDest"/>.</summary>
    /// <param name="src">The source buffer.</param>
    /// <param name="srcOffsetBytes">Byte offset of the first row in the source.</param>
    /// <param name="dst">The destination texture.</param>
    /// <param name="mip">Destination mip level.</param>
    /// <param name="width">Width in texels.</param>
    /// <param name="height">Height in texels.</param>
    /// <param name="srcRowPitchBytes">Source row pitch in bytes.</param>
    /// <param name="bytesPerPixel">Bytes per texel.</param>
    void CopyBufferToTextureMip(IBuffer src, ulong srcOffsetBytes, ITexture dst, uint mip, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel);

    /// <summary>Buffer-to-buffer copy. Both buffers must be in their copy states.</summary>
    /// <param name="src">The source buffer.</param>
    /// <param name="srcOffsetBytes">Source byte offset.</param>
    /// <param name="dst">The destination buffer.</param>
    /// <param name="dstOffsetBytes">Destination byte offset.</param>
    /// <param name="sizeBytes">Byte count.</param>
    void CopyBuffer(IBuffer src, ulong srcOffsetBytes, IBuffer dst, ulong dstOffsetBytes, ulong sizeBytes);

    /// <summary>Opens a named GPU debug region on this list's timeline, paired with <see cref="EndEvent"/>. The graph
    /// compiler wraps each pass at the list level so the region encloses the pass's barriers and draws. Color is ARGB;
    /// 0 = automatic. A no-op unless <see cref="Diagnostics.Profiler.GpuMarkers"/> is set.</summary>
    /// <param name="name">Region name.</param>
    /// <param name="color">ARGB color.</param>
    void BeginEvent(string name, uint color);

    /// <summary>Closes the most recent <see cref="BeginEvent"/> region.</summary>
    void EndEvent();

    /// <summary>Inserts an instantaneous named GPU marker.</summary>
    /// <param name="name">Marker name.</param>
    /// <param name="color">ARGB color.</param>
    void SetMarker(string name, uint color);
}

/// <summary>A GPU submission queue. On single-queue hardware the compute and copy queues alias the graphics queue.</summary>
public interface ICommandQueue
{
    /// <summary>The queue class.</summary>
    QueueKind Kind { get; }

    /// <summary>Acquires a command list to record into.</summary>
    /// <returns>An open command list.</returns>
    ICommandList AcquireCommandList();

    /// <summary>Submits closed command lists.</summary>
    /// <param name="lists">The lists, in execution order.</param>
    /// <param name="waitOn">Fence values to wait for before execution.</param>
    /// <param name="signal">Fence values to signal after execution.</param>
    void Submit(ReadOnlySpan<ICommandList> lists, ReadOnlySpan<FenceWait> waitOn, ReadOnlySpan<FenceSignal> signal);

    /// <summary>Signals the fence's next value on this queue.</summary>
    /// <param name="fence">The fence.</param>
    /// <returns>The value signalled.</returns>
    ulong Signal(IFence fence);

    /// <summary>Blocks until all work submitted to this queue has completed (vkQueueWaitIdle, or a D3D12 signal plus CPU
    /// wait). Used at shutdown so the device is idle before any GPU object, or the device itself, is destroyed.</summary>
    void WaitIdle();

    /// <summary>Opens a named GPU debug region on this queue's timeline (submit and present boundaries), paired with
    /// <see cref="EndEvent"/>. Color is ARGB; 0 = automatic.</summary>
    /// <param name="name">Region name.</param>
    /// <param name="color">ARGB color.</param>
    void BeginEvent(string name, uint color);

    /// <summary>Closes the most recent <see cref="BeginEvent"/> region on this queue.</summary>
    void EndEvent();

    /// <summary>Inserts an instantaneous named GPU marker on this queue's timeline.</summary>
    /// <param name="name">Marker name.</param>
    /// <param name="color">ARGB color.</param>
    void SetMarker(string name, uint color);
}

/// <summary>Optional queue capability for BINARY swapchain synchronization (Vulkan): the acquire and present semaphores
/// the swapchain registers in <see cref="ISwapchain.AcquireNextBackbuffer"/> are attached only to a submit made through
/// <see cref="SubmitPresenting"/>, the one submit that renders the acquired image. Every other submit (a one-shot upload,
/// an initial barrier list) leaves them alone, so it can never take the frame's present sync. Queues without binary
/// swapchain sync (D3D12, headless) do not implement this; <see cref="CommandQueueExtensions.SubmitFrame"/> falls back
/// to a plain <see cref="ICommandQueue.Submit"/>.</summary>
public interface IPresentSyncSubmit
{
    /// <summary>Submits the frame's render work and attaches this frame's acquire wait and present signal, if any.</summary>
    /// <param name="lists">The lists, in execution order.</param>
    /// <param name="waitOn">Fence values to wait for before execution.</param>
    /// <param name="signal">Fence values to signal after execution.</param>
    void SubmitPresenting(ReadOnlySpan<ICommandList> lists, ReadOnlySpan<FenceWait> waitOn, ReadOnlySpan<FenceSignal> signal);
}

/// <summary>Helpers over <see cref="ICommandQueue"/>.</summary>
public static class CommandQueueExtensions
{
    /// <summary>Submits the frame's render work: the one submit that writes the swapchain image acquired this frame.
    /// Anything else uses <see cref="ICommandQueue.Submit"/>.</summary>
    /// <param name="queue">The queue.</param>
    /// <param name="lists">The lists, in execution order.</param>
    /// <param name="waitOn">Fence values to wait for before execution.</param>
    /// <param name="signal">Fence values to signal after execution.</param>
    public static void SubmitFrame(this ICommandQueue queue, ReadOnlySpan<ICommandList> lists, ReadOnlySpan<FenceWait> waitOn, ReadOnlySpan<FenceSignal> signal)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (queue is IPresentSyncSubmit p)
        {
            p.SubmitPresenting(lists, waitOn, signal);
        }
        else
        {
            queue.Submit(lists, waitOn, signal);
        }
    }
}

/// <summary>The device abstraction. It owns object creation; submission and scheduling are centralized in the GPU
/// scheduler (MewStage.Rendering).</summary>
public interface IGraphicsDevice : IDisposable
{
    /// <summary>Capabilities detected at device creation.</summary>
    GpuCapabilities Caps { get; }

    /// <summary>Unified memory (integrated GPUs): buffer creation may skip staging.</summary>
    bool IsUma { get; }

    /// <summary>The graphics queue.</summary>
    ICommandQueue GraphicsQueue { get; }

    /// <summary>The compute queue (may alias <see cref="GraphicsQueue"/>).</summary>
    ICommandQueue ComputeQueue { get; }

    /// <summary>The copy queue (may alias <see cref="GraphicsQueue"/>).</summary>
    ICommandQueue CopyQueue { get; }

    /// <summary>Resources queued by <see cref="DeferDestroy"/> and not yet freed. Devices without a queue report 0.</summary>
    int PendingDeferredDestroys => 0;

    /// <summary>Raised on device loss or removal.</summary>
    event Action? OnDeviceLost;

    /// <summary>Creates a buffer.</summary>
    /// <param name="desc">The creation parameters.</param>
    /// <returns>The buffer.</returns>
    IBuffer CreateBuffer(in BufferDesc desc);

    /// <summary>Creates a texture.</summary>
    /// <param name="desc">The creation parameters.</param>
    /// <returns>The texture.</returns>
    ITexture CreateTexture(in TextureDesc desc);

    /// <summary>Creates a buffer view.</summary>
    /// <param name="buffer">The buffer.</param>
    /// <param name="desc">The view parameters.</param>
    /// <returns>The view.</returns>
    IBufferView CreateView(IBuffer buffer, in BufferViewDesc desc);

    /// <summary>Creates a texture view.</summary>
    /// <param name="texture">The texture.</param>
    /// <param name="desc">The view parameters.</param>
    /// <returns>The view.</returns>
    ITextureView CreateView(ITexture texture, in TextureViewDesc desc);

    /// <summary>Creates a descriptor heap.</summary>
    /// <param name="kind">The heap class.</param>
    /// <param name="capacity">Descriptor capacity.</param>
    /// <param name="shaderVisible">Whether shaders can read from it.</param>
    /// <returns>The heap.</returns>
    IDescriptorHeap CreateDescriptorHeap(DescriptorKind kind, int capacity, bool shaderVisible);

    /// <summary>Creates a pipeline layout.</summary>
    /// <param name="desc">The layout parameters.</param>
    /// <returns>The layout.</returns>
    IPipelineLayout CreatePipelineLayout(in PipelineLayoutDesc desc);

    /// <summary>Creates, or fetches from the cache, an immutable sampler.</summary>
    /// <param name="desc">The sampler parameters.</param>
    /// <returns>The sampler.</returns>
    ISampler CreateSampler(in SamplerDesc desc);

    /// <summary>Creates a timeline fence.</summary>
    /// <param name="initialValue">The initial value.</param>
    /// <returns>The fence.</returns>
    IFence CreateFence(ulong initialValue);

    /// <summary>Creates a timestamp query heap.</summary>
    /// <param name="capacity">Query capacity.</param>
    /// <returns>The heap.</returns>
    IQueryHeap CreateTimestampQueryHeap(int capacity);

    /// <summary>Creates an indirect command signature; null on the baseline, non-null only when
    /// <see cref="GpuCapabilities.IndirectDraw"/> is not <see cref="IndirectTier.None"/>.</summary>
    /// <param name="desc">The signature parameters.</param>
    /// <returns>The signature, or null.</returns>
    ICommandSignature? CreateCommandSignature(in CommandSignatureDesc desc);

    /// <summary>Creates a compute pipeline from cooked bytecode. Built at initialization; the hot path only binds.</summary>
    /// <param name="desc">The pipeline description.</param>
    /// <returns>The pipeline.</returns>
    IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc);

    /// <summary>Creates a graphics pipeline from cooked bytecode.</summary>
    /// <param name="desc">The pipeline description.</param>
    /// <returns>The pipeline.</returns>
    IRenderPipeline CreateRenderPipeline(in RenderPipelineDesc desc);

    /// <summary>Creates a compute recorder over a live command list. The scheduler's compute submission records real
    /// dispatches through it; the headless device returns an inspectable recorder.</summary>
    /// <param name="list">An open command list.</param>
    /// <returns>The recorder.</returns>
    IComputeRecorder CreateComputeRecorder(ICommandList list);

    /// <summary>Queues a resource for destruction after the last referencing frame fence signals. The resource is
    /// stamped with the device's current retire frame (advanced by <see cref="RetireDeferred"/>) and freed only once
    /// the GPU has advanced the frames in flight past it, so a command list still in flight can never reference freed
    /// memory. Use it for every GPU resource that a previously submitted frame may still reference (a grown vertex
    /// buffer, a resized viewport target, a replaced glyph atlas).</summary>
    /// <param name="resource">The resource.</param>
    void DeferDestroy(IGpuResource resource);

    /// <summary>Advances the deferred-destroy retire frame to <paramref name="currentFrame"/> and disposes every resource
    /// deferred at least <paramref name="framesInFlight"/> frames ago. Called once per frame by the scheduler at the start
    /// of the frame, after the slot fence wait.</summary>
    /// <param name="currentFrame">The frame about to be recorded.</param>
    /// <param name="framesInFlight">The scheduler's frames in flight.</param>
    void RetireDeferred(long currentFrame, int framesInFlight);

    /// <summary>Immediately disposes every deferred resource regardless of stamp. Teardown only: the caller must have
    /// drained the GPU first.</summary>
    /// <returns>The number of resources freed.</returns>
    int FlushDeferred();

    /// <summary>Polls the device for loss or removal.</summary>
    /// <returns>The status.</returns>
    DeviceStatus PollStatus();

    /// <summary>Tears down and rebuilds the device and its resources after a loss.</summary>
    /// <returns>True when the device was recreated.</returns>
    bool TryRecreate();

    /// <summary>Attaches a human-readable debug name to a GPU resource so it shows up named in captures and validation
    /// (D3D12 ID3D12Object::SetName; Vulkan vkSetDebugUtilsObjectNameEXT). Best effort: a no-op when no marker backend is
    /// active or the resource is not backend-native.</summary>
    /// <param name="resource">The resource.</param>
    /// <param name="name">The name.</param>
    void SetDebugName(IGpuResource resource, string name);
}

/// <summary>Root-signature / pipeline-layout description: a small set of CBV / SRV / UAV / sampler tables.</summary>
public readonly struct PipelineLayoutDesc
{
    /// <summary>Constant-buffer view count.</summary>
    public int CbvCount { get; init; }

    /// <summary>Structured-buffer shader-resource view count.</summary>
    public int SrvCount { get; init; }

    /// <summary>Sampled-image (texture) shader-resource view count, distinct from <see cref="SrvCount"/>.</summary>
    public int TextureSrvCount { get; init; }

    /// <summary>Unordered-access view count.</summary>
    public int UavCount { get; init; }

    /// <summary>Sampler count.</summary>
    public int SamplerCount { get; init; }

    /// <summary>Root / push constants, as a count of 32-bit values.</summary>
    public int RootConstants32 { get; init; }

    /// <summary>Debug name.</summary>
    public string? DebugName { get; init; }
}

/// <summary>Indirect command signature description (optional tier).</summary>
public readonly struct CommandSignatureDesc
{
    /// <summary>Byte stride between argument records.</summary>
    public uint ByteStride { get; init; }

    /// <summary>The signature describes indexed draws.</summary>
    public bool DrawIndexed { get; init; }

    /// <summary>The signature describes dispatches.</summary>
    public bool Dispatch { get; init; }
}
