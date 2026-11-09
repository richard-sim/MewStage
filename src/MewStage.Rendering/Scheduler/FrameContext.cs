// The render frame context (frames in flight) and the per-frame ring resources.
//
// Two frames in flight by default, configurable to three without structural change. Each context owns everything
// that must not be touched while the GPU reads it: command recording is per frame, the upload ring, descriptor
// sub-range, transient pool and query range are reset on recycle, and a deferred-deletion list is flushed once the
// frame fence proves those resources are idle. Per-frame ring and sub-range allocation means zero heap allocation
// on the render hot path.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using MewStage.Graphics;
using MewStage.Rendering.Graph;

namespace MewStage.Rendering.Scheduler;

/// <summary>A bump allocation from the upload ring: a bindable view plus, when host-visible, a write pointer.</summary>
public readonly struct UploadAllocation
{
    /// <summary>Creates an allocation record.</summary>
    /// <param name="view">The bindable range.</param>
    /// <param name="writePointer">The mapped write pointer, or zero.</param>
    public UploadAllocation(GpuBufferView view, IntPtr writePointer)
    {
        View = view;
        WritePointer = writePointer;
    }

    /// <summary>The bindable range.</summary>
    public GpuBufferView View { get; }

    /// <summary>The mapped write pointer, or zero when the buffer is not host-visible.</summary>
    public IntPtr WritePointer { get; }

    /// <summary>True when <see cref="WritePointer"/> is usable.</summary>
    public bool IsMapped => WritePointer != IntPtr.Zero;

    /// <summary>Writes a blittable value into the mapped region (a no-op when the buffer is not host-visible).</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value.</param>
    public unsafe void Write<T>(in T value)
        where T : unmanaged
    {
        if (WritePointer == IntPtr.Zero)
        {
            return;
        }

        Unsafe.Write((void*)WritePointer, value);
    }
}

/// <summary>The per-frame CPU-to-GPU staging ring: one persistently mapped upload buffer, bump-allocated within a frame
/// and rewound on recycle. No per-frame allocation.</summary>
public sealed class UploadRing : IDisposable
{
    private readonly IntPtr _base;

    /// <summary>Creates the ring.</summary>
    /// <param name="device">The device.</param>
    /// <param name="capacityBytes">Ring capacity in bytes.</param>
    public UploadRing(IGraphicsDevice device, ulong capacityBytes)
    {
        ArgumentNullException.ThrowIfNull(device);
        Capacity = capacityBytes;
        Buffer = device.CreateBuffer(new BufferDesc
        {
            SizeBytes = capacityBytes,
            Memory = MemoryClass.Upload,
            Usage = ResourceUsage.ConstantBuffer | ResourceUsage.ShaderResource | ResourceUsage.CopySource,
            DebugName = "frame-upload-ring",
        });
        _base = (Buffer as IMappedBuffer)?.MappedPointer ?? IntPtr.Zero;
    }

    /// <summary>The backing buffer.</summary>
    public IBuffer Buffer { get; }

    /// <summary>Ring capacity in bytes.</summary>
    public ulong Capacity { get; }

    /// <summary>Bytes allocated this frame.</summary>
    public ulong Used { get; private set; }

    /// <summary>Rewinds the ring for a new frame (the fence proved the previous frame's reads are done).</summary>
    public void Reset()
    {
        Used = 0;
    }

    /// <summary>Bump-allocates an aligned region (the default 256 B is the D3D12 constant-buffer alignment).</summary>
    /// <param name="sizeBytes">Byte count.</param>
    /// <param name="alignment">Alignment in bytes.</param>
    /// <returns>The allocation.</returns>
    /// <exception cref="InvalidOperationException">The ring is full.</exception>
    public UploadAllocation Allocate(ulong sizeBytes, ulong alignment = 256)
    {
        ulong start = AlignUp(Used, alignment);
        if (start + sizeBytes > Capacity)
        {
            throw new InvalidOperationException($"Upload ring overflow ({start + sizeBytes} > {Capacity}). Grow the ring or split the upload.");
        }

        Used = start + sizeBytes;
        IntPtr ptr = _base == IntPtr.Zero ? IntPtr.Zero : _base + (nint)start;
        return new UploadAllocation(new GpuBufferView(Buffer.Handle, start, sizeBytes), ptr);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Buffer.Dispose();
    }

    private static ulong AlignUp(ulong v, ulong a)
    {
        return a <= 1 ? v : (v + (a - 1)) & ~(a - 1);
    }
}

/// <summary>A sub-range of the frame context's timestamp query heap.</summary>
public sealed class QueryRange
{

    /// <summary>Creates the range.</summary>
    /// <param name="heap">The query heap.</param>
    /// <param name="start">First query index.</param>
    /// <param name="capacity">Query count.</param>
    public QueryRange(IQueryHeap heap, int start, int capacity)
    {
        Heap = heap;
        Start = start;
        Capacity = capacity;
    }

    /// <summary>The query heap.</summary>
    public IQueryHeap Heap { get; }

    /// <summary>First query index.</summary>
    public int Start { get; }

    /// <summary>Query count.</summary>
    public int Capacity { get; }

    /// <summary>Queries reserved this frame.</summary>
    public int Used { get; private set; }

    /// <summary>Rewinds the range for a new frame.</summary>
    public void Reset()
    {
        Used = 0;
    }

    /// <summary>Reserves a timestamp slot.</summary>
    /// <returns>Its absolute index in the heap, or -1 when the range is exhausted.</returns>
    public int Next()
    {
        return Used >= Capacity ? -1 : Start + Used++;
    }
}

/// <summary>A bump descriptor allocator over this frame's sub-range of the shared shader-visible heap.</summary>
public sealed class DescriptorSubAllocator
{
    private int _used;

    /// <summary>Creates the allocator.</summary>
    /// <param name="range">The frame's sub-range.</param>
    public DescriptorSubAllocator(DescriptorRange range)
    {
        Range = range;
    }

    /// <summary>The frame's sub-range.</summary>
    public DescriptorRange Range { get; }

    /// <summary>Rewinds the allocator for a new frame.</summary>
    public void Reset()
    {
        _used = 0;
    }

    /// <summary>Allocates a contiguous range.</summary>
    /// <param name="count">Descriptor count.</param>
    /// <returns>The range.</returns>
    /// <exception cref="InvalidOperationException">The sub-range is exhausted.</exception>
    public DescriptorRange Allocate(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        if (_used + count > Range.Count)
        {
            throw new InvalidOperationException("Frame descriptor sub-range exhausted.");
        }

        DescriptorRange r = new(Range.HeapHandle, Range.Start + _used, count);
        _used += count;
        return r;
    }
}

/// <summary>The pool of backing resources for render-graph transient slots. The compiler emits a minimal slot count
/// after aliasing; the pool rents one device resource per slot (reused across frames by shape) and binds its handle
/// into the compiled graph. <see cref="Reset"/> returns the rentals to the pool.</summary>
public sealed class TransientPool : IDisposable
{
    private readonly IGraphicsDevice _device;
    private readonly Dictionary<long, Stack<IGpuResource>> _free = [];
    private readonly List<IGpuResource> _inUse = [];
    private readonly List<long> _rentedKey = [];

    /// <summary>Creates the pool.</summary>
    /// <param name="device">The device.</param>
    public TransientPool(IGraphicsDevice device)
    {
        _device = device;
    }

    /// <summary>Allocates and binds a backing resource for every transient slot of the compiled graph.</summary>
    /// <param name="graph">The compiled graph.</param>
    public void Populate(CompiledRenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        for (int slot = 0; slot < graph.TransientSlotCount; slot++)
        {
            IGpuResource res = graph.SlotKind(slot) == RgResourceKind.Texture
                ? RentTexture(graph.SlotTexture(slot))
                : RentBuffer(graph.SlotBuffer(slot));
            graph.BindTransientSlot(slot, res.Handle);
        }
    }

    /// <summary>Returns every rental to the free pool for reuse next frame (the device allocations stay alive).</summary>
    public void Reset()
    {
        for (int i = 0; i < _inUse.Count; i++)
        {
            long key = _rentedKey[i];
            if (!_free.TryGetValue(key, out Stack<IGpuResource>? stack))
            {
                stack = new Stack<IGpuResource>();
                _free[key] = stack;
            }

            stack.Push(_inUse[i]);
        }

        _inUse.Clear();
        _rentedKey.Clear();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (IGpuResource r in _inUse)
        {
            r.Dispose();
        }

        foreach (Stack<IGpuResource> stack in _free.Values)
        {
            foreach (IGpuResource r in stack)
            {
                r.Dispose();
            }
        }

        _inUse.Clear();
        _free.Clear();
    }

    private static ResourceUsage UsageForFormat(Format f)
    {
        if (IsDepth(f))
        {
            // A STENCIL depth (D24S8) is used for stencil operations (the UI's path clip and stencil-then-cover), never
            // sampled. Do not add ShaderResource: on D3D12 a depth + SRV texture is forced to R32_Typeless with a D32
            // depth view, which DROPS the stencil aspect and breaks every stencil operation on real hardware.
            // Depth-only formats (D32) keep ShaderResource so a later pass can sample them.
            return HasStencil(f) ? ResourceUsage.DepthStencil : ResourceUsage.DepthStencil | ResourceUsage.ShaderResource;
        }

        return ResourceUsage.RenderTarget | ResourceUsage.ShaderResource | ResourceUsage.UnorderedAccess;
    }

    private static bool IsDepth(Format f)
    {
        return f is Format.D32_Float or Format.D24_UNorm_S8_UInt or Format.D32_Float_S8_UInt;
    }

    private static bool HasStencil(Format f)
    {
        return f is Format.D24_UNorm_S8_UInt or Format.D32_Float_S8_UInt;
    }

    private static long TexKey(in RgTextureDesc d)
    {
        return HashCodeToLong(HashCode.Combine((int)d.Format, d.Width, d.Height, Math.Max(1u, d.SampleCount), Math.Max(1u, d.ArrayLayers), Math.Max(1u, d.MipLevels), d.ClearColor));
    }

    private static long BufKey(in RgBufferDesc d)
    {
        return HashCodeToLong(HashCode.Combine(0x42, d.SizeBytes, d.StructureStride));
    }

    private static long HashCodeToLong(int h)
    {
        return unchecked((uint)h);
    }

    private IGpuResource RentTexture(in RgTextureDesc d)
    {
        long key = TexKey(d);
        IGpuResource res = Take(key) ?? _device.CreateTexture(new TextureDesc
        {
            Dimension = TextureDimension.Tex2D,
            Format = d.Format,
            Width = d.Width,
            Height = d.Height,
            DepthOrArrayLayers = Math.Max(1u, d.ArrayLayers),
            MipLevels = Math.Max(1u, d.MipLevels),
            SampleCount = Math.Max(1u, d.SampleCount),
            Usage = UsageForFormat(d.Format),
            DebugName = d.Name,
            ClearColor = d.ClearColor,
        });
        _inUse.Add(res);
        _rentedKey.Add(key);
        return res;
    }

    private IGpuResource RentBuffer(in RgBufferDesc d)
    {
        long key = BufKey(d);
        IGpuResource res = Take(key) ?? _device.CreateBuffer(new BufferDesc
        {
            SizeBytes = d.SizeBytes,
            StructureStride = d.StructureStride,
            Memory = MemoryClass.DeviceLocal,
            Usage = ResourceUsage.UnorderedAccess | ResourceUsage.ShaderResource | ResourceUsage.CopySource | ResourceUsage.CopyDest,
            DebugName = d.Name,
        });
        _inUse.Add(res);
        _rentedKey.Add(key);
        return res;
    }

    private IGpuResource? Take(long key)
    {
        return _free.TryGetValue(key, out Stack<IGpuResource>? stack) && stack.Count > 0 ? stack.Pop() : null;
    }
}

/// <summary>One frame in flight. Owns its fence, upload ring, transient pool, descriptor sub-range, query range and
/// deferred-deletion list.</summary>
public sealed class FrameContext : IDisposable
{
    /// <summary>Creates the context.</summary>
    /// <param name="index">The slot index.</param>
    /// <param name="fence">The slot's fence.</param>
    /// <param name="upload">The slot's upload ring.</param>
    /// <param name="transient">The slot's transient pool.</param>
    /// <param name="descriptors">The slot's descriptor sub-range.</param>
    /// <param name="timestamps">The slot's query range.</param>
    public FrameContext(int index, IFence fence, UploadRing upload, TransientPool transient, DescriptorSubAllocator descriptors, QueryRange timestamps)
    {
        Index = index;
        FrameFence = fence;
        Upload = upload;
        Transient = transient;
        Descriptors = descriptors;
        Timestamps = timestamps;
    }

    /// <summary>The slot index (0 .. frames in flight - 1).</summary>
    public int Index { get; }

    /// <summary>The slot's fence, signalled at the end of each frame recorded in the slot.</summary>
    public IFence FrameFence { get; }

    /// <summary>The fence value the slot's last frame signals.</summary>
    public ulong FenceValue { get; internal set; }

    /// <summary>The slot's upload ring.</summary>
    public UploadRing Upload { get; }

    /// <summary>The slot's transient pool.</summary>
    public TransientPool Transient { get; }

    /// <summary>The slot's descriptor sub-range.</summary>
    public DescriptorSubAllocator Descriptors { get; }

    /// <summary>The slot's timestamp query range.</summary>
    public QueryRange Timestamps { get; }

    /// <summary>Resources retired while this slot was active, disposed when the slot is next recycled.</summary>
    public List<IGpuResource> PendingDeletes { get; } = [];

    /// <summary>The slot's compute recorder: created on the slot's first submit, re-targeted to each frame's graphics
    /// command list by the scheduler, never allocated on the per-frame path.</summary>
    public IComputeRecorder? ComputeRecorder { get; internal set; }

    /// <inheritdoc />
    public void Dispose()
    {
        Upload.Dispose();
        Transient.Dispose();
        FrameFence.Dispose();
    }

    /// <summary>Resets the per-frame rings and sub-ranges for reuse.</summary>
    internal void ResetForFrame()
    {
        Upload.Reset();
        Transient.Reset();
        Descriptors.Reset();
        Timestamps.Reset();
    }
}
