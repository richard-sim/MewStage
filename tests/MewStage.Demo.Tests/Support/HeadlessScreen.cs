using System;
using System.Diagnostics;

using Aprillz.MewUI;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Headless;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;
using MewStage.Ui.Backend;

namespace MewStage.Demo.Tests.Support;

/// <summary>
/// A demo screen on the headless device (no GPU, no SDL): an EmbeddedUiHost at a fixed surface size showing a window
/// stack, with frames stepped at chosen timestamps. Layout, bindings, styles, templates, transitions, focus and the
/// input bridge all run for real; only the pixels are missing. The journeys cover those on lavapipe.
/// </summary>
internal sealed class HeadlessScreen : IDisposable
{
    private readonly HeadlessGraphicsList _cmd = new();

    public HeadlessScreen(int width, int height, bool designSpace = false)
    {
        Device = new HeadlessDevice();
        Host = new EmbeddedUiHost(Device, [0, 0, 0, 0], [0, 0, 0, 0], flipClipY: false, framesInFlight: 2, registerFonts: DemoFonts.Register);
        if (designSpace)
        {
            Host.UseDesignSpace = true;
            Host.DesignWidth = Overlay.HudView.DesignWidth;
            Host.DesignHeight = Overlay.HudView.DesignHeight;
        }

        Host.Resize(width, height, 1.0);
    }

    public HeadlessDevice Device { get; }

    public EmbeddedUiHost Host { get; }

    public SdlInputBridge Bridge { get; } = new() { Scale = 1f };

    public GamepadFocusNavigator Navigator { get; } = new();

    /// <summary>Stopwatch ticks per millisecond.</summary>
    public static long Ms => Stopwatch.Frequency / 1000;

    /// <summary>The timestamp the last frame ran at.</summary>
    public long Now { get; private set; } = Stopwatch.GetTimestamp();

    public void Show(params Window[] bottomToTop)
    {
        Host.SetWindows(bottomToTop);
        Bridge.Scale = Host.UseDesignSpace ? (float)Host.EffectiveScale : 1f;
    }

    /// <summary>Run one Continuous frame <paramref name="advanceMs"/> after the previous one; true when the host rendered.</summary>
    public bool Frame(long advanceMs = 16)
    {
        Now += advanceMs * Ms;
        return Run(UiInvalidationMode.Continuous);
    }

    /// <summary>Run one OnRequest frame (false when nothing asked for a redraw).</summary>
    public bool FrameOnRequest(long advanceMs = 16)
    {
        Now += advanceMs * Ms;
        return Run(UiInvalidationMode.OnRequest);
    }

    public void Dispose()
    {
        Navigator.Dispose();
        Host.Dispose();
        Device.Dispose();
        _cmd.Dispose();
    }

    private bool Run(UiInvalidationMode mode)
    {
        var graph = new RenderGraph();
        if (!Host.TickAndRender(graph, 0xBB, mode, clearBackground: true, animationTimestamp: Now))
        {
            return false;
        }

        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(graph);
        using var pool = new TransientPool(Device);
        pool.Populate(compiled);
        compiled.Execute(_cmd, 0);
        return true;
    }
}

/// <summary>A command list + graphics recorder for the headless device that records nothing but counts draws.</summary>
internal sealed class HeadlessGraphicsList : ICommandList, IGraphicsRecorder
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
