// A headless compute recorder that captures SetPipeline / Bind / Dispatch / Barrier / timestamp calls in order, so
// compute passes are unit-testable with no GPU and the scheduler's compute timeline can be exercised.

using System;
using System.Collections.Generic;

using MewStage.Graphics;

namespace MewStage.Rendering.Compute;

/// <summary>An inspectable <see cref="IComputeRecorder"/> for tests and the headless device.</summary>
public sealed class RecordingComputeRecorder : IRetargetableComputeRecorder
{
    /// <summary><see cref="DispatchCall.GroupsX"/> value that marks an INDIRECT dispatch (the counts live on the GPU).</summary>
    public const uint IndirectMarker = uint.MaxValue;

    private ComputePipeline _pso;
    private int _tsSlot;

    /// <summary>The binds recorded, in order.</summary>
    public List<BindCall> Binds { get; } = [];

    /// <summary>The dispatches recorded, in order.</summary>
    public List<DispatchCall> Dispatches { get; } = [];

    /// <summary>The barriers recorded, in order.</summary>
    public List<ResourceBarrierDesc> Barriers { get; } = [];

    /// <summary>The timestamp labels recorded, in order.</summary>
    public List<string> TimestampLabels { get; } = [];

    /// <inheritdoc />
    public ICommandList CommandList { get => field ?? throw new InvalidOperationException("RecordingComputeRecorder has no command list"); private set; }

    /// <inheritdoc />
    public void Retarget(ICommandList cmd)
    {
        CommandList = cmd;
    }

    /// <inheritdoc />
    public void SetPipeline(ComputePipeline pso)
    {
        _pso = pso;
    }

    /// <inheritdoc />
    public void BindSrv(int slot, in GpuBufferView view)
    {
        Binds.Add(new("srv", slot, view.Handle));
    }

    /// <inheritdoc />
    public void BindUav(int slot, in GpuBufferView view)
    {
        Binds.Add(new("uav", slot, view.Handle));
    }

    /// <inheritdoc />
    public void BindCbv(int slot, in GpuBufferView view)
    {
        Binds.Add(new("cbv", slot, view.Handle));
    }

    /// <inheritdoc />
    public void PushConstants<T>(in T data)
        where T : unmanaged
    {
        // Captured by value by a real backend; nothing to inspect here.
    }

    /// <inheritdoc />
    public void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
    {
        Dispatches.Add(new(_pso.Handle, groupsX, groupsY, groupsZ));
    }

    /// <inheritdoc />
    public void DispatchIndirect(in GpuBufferView args, uint offsetBytes)
    {
        Binds.Add(new("indirect", (int)offsetBytes, args.Handle));
        Dispatches.Add(new(_pso.Handle, IndirectMarker, 0, 0));
    }

    /// <inheritdoc />
    public void BindTexture(int slot, ITextureView srv)
    {
        Binds.Add(new("tex", slot, srv.Handle));
    }

    /// <inheritdoc />
    public void BindSampler(int slot, ISampler sampler)
    {
        Binds.Add(new("sampler", slot, sampler.Handle));
    }

    /// <inheritdoc />
    public void Barrier(in ResourceBarrierDesc desc)
    {
        Barriers.Add(desc);
    }

    /// <inheritdoc />
    public ScopedTimestamp BeginTimestamp(string label)
    {
        TimestampLabels.Add(label);
        return new ScopedTimestamp(this, _tsSlot++);
    }

    /// <inheritdoc />
    public void EndTimestamp(int slot)
    {
    }

    /// <summary>A recorded dispatch. <see cref="GroupsX"/> = <see cref="IndirectMarker"/> marks an indirect dispatch.</summary>
    /// <param name="Pso">The bound pipeline handle.</param>
    /// <param name="GroupsX">Groups along x.</param>
    /// <param name="GroupsY">Groups along y.</param>
    /// <param name="GroupsZ">Groups along z.</param>
    public readonly record struct DispatchCall(ulong Pso, uint GroupsX, uint GroupsY, uint GroupsZ);

    /// <summary>A recorded bind.</summary>
    /// <param name="Kind">"srv", "uav", "cbv", "tex", "sampler" or "indirect".</param>
    /// <param name="Slot">The slot (the byte offset for "indirect").</param>
    /// <param name="Handle">The bound resource's handle.</param>
    public readonly record struct BindCall(string Kind, int Slot, ulong Handle);
}
