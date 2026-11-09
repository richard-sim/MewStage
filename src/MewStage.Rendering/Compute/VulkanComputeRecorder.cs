// The Vulkan compute recorder: adapts the IComputeRecorder facade onto the VulkanCommandList compute path (descriptor-set
// flush at the compute bind point, vkCmdDispatch), mirroring the D3D12 recorder.

using System;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Vulkan;

namespace MewStage.Rendering.Compute;

internal sealed class VulkanComputeRecorder : IRetargetableComputeRecorder
{
    private readonly VulkanDevice _dev;
    private VulkanCommandList _cmd;
    private int _tsSlot;

    public VulkanComputeRecorder(VulkanDevice dev, VulkanCommandList cmd)
    {
        _dev = dev;
        _cmd = cmd;
    }

    public ICommandList CommandList => _cmd;

    public void Retarget(ICommandList cmd)
    {
        _cmd = (VulkanCommandList)cmd;
        _tsSlot = 0;
    }

    public void SetPipeline(ComputePipeline pso)
    {
        if (_dev.Lookup(pso.Handle) is VkComputePipeline p)
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
