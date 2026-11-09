using System;
using System.Collections.Generic;

using MewStage.Graphics;

namespace MewStage.Ui.Tests.Support;

/// <summary>An <see cref="IGraphicsRecorder"/> that counts what the NanoVG renderer records; nothing is drawn.</summary>
public sealed class RecordingRecorder : IGraphicsRecorder
{
    public int DrawCalls { get; private set; }

    public int VertexBufferBinds { get; private set; }

    public int ConstantBufferBinds { get; private set; }

    public int TextureBinds { get; private set; }

    public int PipelineSets { get; private set; }

    public int StencilRefSets { get; private set; }

    public uint TotalVertices { get; private set; }

    public List<ulong> CbvOffsets { get; } = [];

    public void Reset()
    {
        DrawCalls = VertexBufferBinds = ConstantBufferBinds = TextureBinds = PipelineSets = StencilRefSets = 0;
        TotalVertices = 0;
        CbvOffsets.Clear();
    }

    public void BeginRendering(ReadOnlySpan<ColorAttachment> colors, in DepthAttachment depth, uint width, uint height) { }

    public void EndRendering() { }

    public void SetPipeline(IRenderPipeline pso)
    {
        PipelineSets++;
    }

    public void SetViewportScissor(float x, float y, float width, float height, float minDepth = 0f, float maxDepth = 1f) { }

    public void SetScissor(int x, int y, uint width, uint height) { }

    public void SetStencilReference(uint reference)
    {
        StencilRefSets++;
    }

    public void BindDescriptorTable(int rootSlot, DescriptorRange range) { }

    public void BindConstantBuffer(int slot, in GpuBufferView cbv)
    {
        ConstantBufferBinds++;
        CbvOffsets.Add(cbv.Offset);
    }

    public void BindTexture(int slot, ITextureView srv)
    {
        TextureBinds++;
    }

    public void BindSampler(int slot, ISampler sampler) { }

    public void BindStorageBuffer(int slot, in GpuBufferView buffer, bool uav) { }

    public void BindVertexBuffer(int slot, in GpuBufferView vb, uint stride)
    {
        VertexBufferBinds++;
    }

    public void BindIndexBuffer(in GpuBufferView ib, bool index32) { }

    public void PushConstants<T>(in T data)
        where T : unmanaged
    {
    }

    public void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance)
    {
        DrawCalls++;
        TotalVertices += vertexCount;
    }

    public void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance) { }

    public void DrawIndexedIndirect(in GpuBufferView args, uint offsetBytes, uint drawCount, uint stride) { }

    public void BeginEvent(string name, uint color) { }

    public void EndEvent() { }

    public void SetMarker(string name, uint color) { }
}
