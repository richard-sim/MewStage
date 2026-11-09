using System;
using System.Collections.Generic;

using MewStage.Graphics;

namespace MewStage.Rendering.Tests;

/// <summary>Headless recording command list for graph-executor tests (no GPU). Captures barriers and the order of
/// events; every copy and draw is a no-op.</summary>
public sealed class RecordingCommandList : ICommandList, IGraphicsRecorder
{
    public RecordingCommandList(QueueKind queue = QueueKind.Graphics)
    {
        Queue = queue;
    }

    public QueueKind Queue { get; }

    public List<ResourceBarrierDesc> Barriers { get; } = [];

    public List<(string Event, int Arg)> Events { get; } = [];

    public int Draws { get; private set; }

    public bool Opened { get; private set; }

    public bool Closed { get; private set; }

    public void Begin()
    {
        Opened = true;
    }

    public void Close()
    {
        Closed = true;
    }

    public void ResourceBarrier(ReadOnlySpan<ResourceBarrierDesc> barriers)
    {
        foreach (ResourceBarrierDesc b in barriers)
        {
            Barriers.Add(b);
            Events.Add(("barrier", (int)b.ResourceHandle));
        }
    }

    public void CopyBufferToTexture(IBuffer src, ITexture dst, uint width, uint height) { }

    public void CopyBufferToTextureRegion(IBuffer src, ITexture dst, uint dstX, uint dstY, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel) { }

    public void CopyTextureToBuffer(ITexture src, IBuffer dst, uint width, uint height) { }

    public void CopyBufferToTextureMip(IBuffer src, ulong srcOffsetBytes, ITexture dst, uint mip, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel) { }

    public void CopyBuffer(IBuffer src, ulong srcOffsetBytes, IBuffer dst, ulong dstOffsetBytes, ulong sizeBytes)
    {
        Events.Add(("copy", (int)sizeBytes));
    }

    public void WriteTimestamp(IQueryHeap heap, int index)
    {
        Events.Add(("timestamp", index));
    }

    // GPU markers are not recorded into Events: the compiler brackets every pass and tests assert the event order.
    public void BeginEvent(string name, uint color) { }

    public void EndEvent() { }

    public void SetMarker(string name, uint color) { }

    public void Dispose() { }

    public void Mark(string name)
    {
        Events.Add((name, 0));
    }

    public void BeginRendering(ReadOnlySpan<ColorAttachment> colors, in DepthAttachment depth, uint width, uint height)
    {
        Events.Add(("begin-rendering", colors.Length));
    }

    public void EndRendering()
    {
        Events.Add(("end-rendering", 0));
    }

    public void SetPipeline(IRenderPipeline pso) { }

    public void SetViewportScissor(float x, float y, float width, float height, float minDepth = 0f, float maxDepth = 1f) { }

    public void SetScissor(int x, int y, uint width, uint height) { }

    public void SetStencilReference(uint reference) { }

    public void BindDescriptorTable(int rootSlot, DescriptorRange range) { }

    public void BindConstantBuffer(int slot, in GpuBufferView cbv) { }

    public void BindTexture(int slot, ITextureView srv) { }

    public void BindSampler(int slot, ISampler sampler) { }

    public void BindStorageBuffer(int slot, in GpuBufferView buffer, bool uav) { }

    public void BindVertexBuffer(int slot, in GpuBufferView vb, uint stride) { }

    public void BindIndexBuffer(in GpuBufferView ib, bool index32) { }

    public void PushConstants<T>(in T data)
        where T : unmanaged
    {
    }

    public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        Draws++;
    }

    public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        Draws++;
    }

    public void DrawIndexedIndirect(in GpuBufferView args, uint offsetBytes, uint drawCount, uint stride)
    {
        Draws++;
    }
}
