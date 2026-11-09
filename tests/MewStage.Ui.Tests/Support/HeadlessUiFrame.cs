using System;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Headless;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;
using MewStage.Ui.Backend;

namespace MewStage.Ui.Tests.Support;

/// <summary>
/// One host frame on the headless device: the host wires its ui pass into a graph, the compiler builds it, a
/// TransientPool backs the stencil and the pass EXECUTES onto a recording command list, so the windows' contexts are
/// created, text runs are measured and the renderer flushes exactly as on a GPU (minus the pixels).
/// </summary>
public static class HeadlessUiFrame
{
    public const ulong Target = 0xBB;

    /// <summary>Runs one frame; returns false when the host decided not to render (OnRequest, nothing changed).</summary>
    public static bool Run(HeadlessDevice device, EmbeddedUiHost host, UiInvalidationMode mode, HeadlessGraphicsList cmd, bool clearBackground = true, long? timestamp = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(cmd);
        var graph = new RenderGraph();
        if (!host.TickAndRender(graph, Target, mode, clearBackground, timestamp))
        {
            return false;
        }

        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(graph);
        using var pool = new TransientPool(device);
        pool.Populate(compiled);
        compiled.Execute(cmd, 0);
        return true;
    }
}

/// <summary>A command list + graphics recorder for the headless device that records nothing but counts draws.</summary>
public sealed class HeadlessGraphicsList : ICommandList, IGraphicsRecorder
{
    public int Draws { get; private set; }

    public int Barriers { get; private set; }

    public int Copies { get; private set; }

    public QueueKind Queue => QueueKind.Graphics;

    public void Reset()
    {
        Draws = Barriers = Copies = 0;
    }

    public void Begin() { }

    public void Close() { }

    public void ResourceBarrier(ReadOnlySpan<ResourceBarrierDesc> barriers)
    {
        Barriers += barriers.Length;
    }

    public void WriteTimestamp(IQueryHeap heap, int index) { }

    public void CopyBufferToTexture(IBuffer src, ITexture dst, uint width, uint height)
    {
        Copies++;
    }

    public void CopyTextureToBuffer(ITexture src, IBuffer dst, uint width, uint height)
    {
        Copies++;
    }

    public void CopyBufferToTextureRegion(IBuffer src, ITexture dst, uint dstX, uint dstY, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel)
    {
        Copies++;
    }

    public void CopyBufferToTextureMip(IBuffer src, ulong srcOffsetBytes, ITexture dst, uint mip, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel)
    {
        Copies++;
    }

    public void CopyBuffer(IBuffer src, ulong srcOffsetBytes, IBuffer dst, ulong dstOffsetBytes, ulong sizeBytes)
    {
        Copies++;
    }

    public void BeginEvent(string name, uint color) { }

    public void EndEvent() { }

    public void SetMarker(string name, uint color) { }

    public void Dispose() { }

    public void BeginRendering(ReadOnlySpan<ColorAttachment> colors, in DepthAttachment depth, uint width, uint height) { }

    public void EndRendering() { }

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
