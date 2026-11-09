// The D3D12 compute recorder: adapts the IComputeRecorder facade onto the Dx12CommandList compute path (compute root
// signature, compute root descriptor tables, Dispatch), mirroring the Vulkan recorder.

using System;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Dx12;

namespace MewStage.Rendering.Compute;

internal sealed class Dx12ComputeRecorder : IRetargetableComputeRecorder
{
    private readonly Dx12Device _dev;
    private Dx12CommandList _cmd;
    private int _tsSlot;

    public Dx12ComputeRecorder(Dx12Device dev, Dx12CommandList cmd)
    {
        _dev = dev;
        _cmd = cmd;
    }

    public ICommandList CommandList => _cmd;

    public void Retarget(ICommandList cmd)
    {
        _cmd = (Dx12CommandList)cmd;
        _tsSlot = 0;
    }

    public void SetPipeline(ComputePipeline pso)
    {
        if (_dev.Lookup(pso.Handle) is D12ComputePipeline p)
        {
            _cmd.SetComputePipeline(p);
        }
    }

    public void BindCbv(int slot, in GpuBufferView view)
    {
        _cmd.BindConstantBuffer(slot, view);
    }

    public void BindSrv(int slot, in GpuBufferView view)
    {
        _cmd.BindStorageBuffer(slot, view, uav: false);
    }

    public void BindUav(int slot, in GpuBufferView view)
    {
        _cmd.BindStorageBuffer(slot, view, uav: true);
    }

    public void PushConstants<T>(in T data)
        where T : unmanaged
    {
        _cmd.PushConstants(in data);
    }

    public void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
    {
        _cmd.DispatchCompute(groupsX, groupsY, groupsZ);
    }

    public void DispatchIndirect(in GpuBufferView args, uint offsetBytes)
    {
        _cmd.DispatchComputeIndirect(in args, offsetBytes);
    }

    public void BindTexture(int slot, ITextureView srv)
    {
        _cmd.BindTexture(slot, srv);
    }

    public void BindSampler(int slot, ISampler sampler)
    {
        _cmd.BindSampler(slot, sampler);
    }

    public void Barrier(in ResourceBarrierDesc desc)
    {
        Span<ResourceBarrierDesc> one = [desc];
        _cmd.ResourceBarrier(one);
    }

    public ScopedTimestamp BeginTimestamp(string label)
    {
        return new ScopedTimestamp(this, _tsSlot++);
    }

    public void EndTimestamp(int slot)
    {
    }
}
