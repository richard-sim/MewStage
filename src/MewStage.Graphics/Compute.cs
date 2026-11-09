// Compute pass contracts. A compute workload never touches a raw command list; it implements IComputePass against
// the recorder facade the scheduler provides, and the scheduler batches every Record call of a frame into one
// command list. IComputePass is a stable object, not a per-frame lambda, so submission allocates nothing.

using System;

namespace MewStage.Graphics;

/// <summary>Opaque handle to a compute pipeline created at initialization; the hot path only binds it.</summary>
public readonly struct ComputePipeline : IEquatable<ComputePipeline>
{
    /// <summary>Wraps a backend handle.</summary>
    /// <param name="handle">The backend handle.</param>
    public ComputePipeline(ulong handle)
    {
        Handle = handle;
    }

    /// <summary>The backend handle.</summary>
    public ulong Handle { get; }

    /// <summary>True when the handle refers to a pipeline.</summary>
    public bool IsValid => Handle != 0;

    /// <summary>Value equality.</summary>
    public static bool operator ==(ComputePipeline left, ComputePipeline right)
    {
        return left.Equals(right);
    }

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ComputePipeline left, ComputePipeline right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public bool Equals(ComputePipeline other)
    {
        return Handle == other.Handle;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is ComputePipeline c && Equals(c);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return Handle.GetHashCode();
    }
}

/// <summary>Per-frame context handed to a compute pass.</summary>
public readonly struct ComputeFrameContext
{
    /// <summary>The frame index.</summary>
    public ulong FrameIndex { get; init; }

    /// <summary>How the pass is being submitted.</summary>
    public ComputeSubmissionMode Mode { get; init; }
}

/// <summary>A scoped timestamp pair around a dispatch chain. Returned by <see cref="IComputeRecorder.BeginTimestamp"/>;
/// disposing it writes the closing timestamp.</summary>
public readonly struct ScopedTimestamp : IDisposable
{
    private readonly IComputeRecorder _recorder;

    /// <summary>Creates the scope.</summary>
    /// <param name="recorder">The recorder.</param>
    /// <param name="slot">The timestamp slot.</param>
    public ScopedTimestamp(IComputeRecorder recorder, int slot)
    {
        _recorder = recorder;
        Slot = slot;
    }

    /// <summary>The timestamp slot.</summary>
    public int Slot { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _recorder.EndTimestamp(Slot);
    }
}

/// <summary>The backend-agnostic facade over a compute encoder. Binding allocates nothing: value views, cached pipelines.</summary>
public interface IComputeRecorder
{
    /// <summary>Binds a compute pipeline.</summary>
    /// <param name="pso">The pipeline.</param>
    void SetPipeline(ComputePipeline pso);

    /// <summary>Binds a buffer as a shader resource.</summary>
    /// <param name="slot">The t-slot.</param>
    /// <param name="view">The buffer range.</param>
    void BindSrv(int slot, in GpuBufferView view);

    /// <summary>Binds a buffer for unordered access.</summary>
    /// <param name="slot">The u-slot.</param>
    /// <param name="view">The buffer range.</param>
    void BindUav(int slot, in GpuBufferView view);

    /// <summary>Binds a constant buffer.</summary>
    /// <param name="slot">The b-slot.</param>
    /// <param name="view">The buffer range.</param>
    void BindCbv(int slot, in GpuBufferView view);

    /// <summary>Writes root / push constants.</summary>
    /// <typeparam name="T">The constant block type.</typeparam>
    /// <param name="data">The constants.</param>
    void PushConstants<T>(in T data)
        where T : unmanaged;

    /// <summary>Dispatches thread groups.</summary>
    /// <param name="groupsX">Groups along x.</param>
    /// <param name="groupsY">Groups along y.</param>
    /// <param name="groupsZ">Groups along z.</param>
    void Dispatch(uint groupsX, uint groupsY, uint groupsZ);

    /// <summary>Dispatches with the group counts read from a GPU buffer (a uint3 at <paramref name="offsetBytes"/>; the
    /// buffer is in <see cref="ResourceState.IndirectArgument"/>). Optional tier (<see cref="GpuCapabilities.IndirectDraw"/>).</summary>
    /// <param name="args">The arguments buffer.</param>
    /// <param name="offsetBytes">Byte offset of the uint3.</param>
    void DispatchIndirect(in GpuBufferView args, uint offsetBytes);

    /// <summary>Binds a sampled texture to a compute t-slot (after the buffer SRVs).</summary>
    /// <param name="slot">The t-slot.</param>
    /// <param name="srv">The texture view.</param>
    void BindTexture(int slot, ITextureView srv);

    /// <summary>Binds a sampler to a compute s-slot.</summary>
    /// <param name="slot">The s-slot.</param>
    /// <param name="sampler">The sampler.</param>
    void BindSampler(int slot, ISampler sampler);

    /// <summary>A UAV or transition barrier between dependent dispatches.</summary>
    /// <param name="desc">The barrier.</param>
    void Barrier(in ResourceBarrierDesc desc);

    /// <summary>Opens a labeled timestamp pair.</summary>
    /// <param name="label">The label.</param>
    /// <returns>A scope whose disposal writes the closing timestamp.</returns>
    ScopedTimestamp BeginTimestamp(string label);

    /// <summary>Writes the closing timestamp of <paramref name="slot"/>.</summary>
    /// <param name="slot">The slot returned by <see cref="BeginTimestamp"/>.</param>
    void EndTimestamp(int slot);
}

/// <summary>A compute workload.</summary>
public interface IComputePass
{
    /// <summary>A stable name (pipeline key and timestamp label).</summary>
    string Name { get; }

    /// <summary>Records the pass.</summary>
    /// <param name="recorder">The recorder.</param>
    /// <param name="context">The frame context.</param>
    void Record(IComputeRecorder recorder, in ComputeFrameContext context);
}

/// <summary>Group-count helper: groups = ceil(itemCount / threadGroupSize).</summary>
public static class ComputeDispatch
{
    /// <summary>Thread groups needed to cover <paramref name="itemCount"/> items.</summary>
    /// <param name="itemCount">Item count.</param>
    /// <param name="threadGroupSize">Threads per group along the dispatched axis.</param>
    /// <returns>The group count (0 for no items).</returns>
    public static uint GroupCount(int itemCount, int threadGroupSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadGroupSize);
        if (itemCount <= 0)
        {
            return 0;
        }

        return (uint)((itemCount + threadGroupSize - 1) / threadGroupSize);
    }
}
