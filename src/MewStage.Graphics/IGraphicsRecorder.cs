// The graphics command recorder: the facade a render-graph pass records draws through. The backend command list
// implements it. Dynamic-rendering style: BeginRendering binds the color and depth attachments (with optional
// clears), the pass draws, EndRendering. Barriers are still synthesized by the graph compiler and issued through
// ICommandList; a recorder never issues barriers by hand. Reversed-Z: depth clears to 0.

using System;

namespace MewStage.Graphics;

/// <summary>A clear color. A record struct so it can key a transient-pool lookup (<see cref="TextureDesc.ClearColor"/>).</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
/// <param name="A">Alpha.</param>
public readonly record struct ClearColor(float R, float G, float B, float A)
{
    /// <summary>Opaque black.</summary>
    public static ClearColor Black => new(0, 0, 0, 1);

    /// <summary>Transparent black (every channel zero), for premultiplied-alpha targets.</summary>
    public static ClearColor Transparent => new(0, 0, 0, 0);
}

/// <summary>A color attachment for <see cref="IGraphicsRecorder.BeginRendering"/>: a physical texture handle plus load / clear.</summary>
public readonly struct ColorAttachment
{
    /// <summary>The texture's backend handle.</summary>
    public ulong TextureHandle { get; init; }

    /// <summary>Clear on load (else the previous contents are loaded).</summary>
    public bool Clear { get; init; }

    /// <summary>The clear color when <see cref="Clear"/> is set.</summary>
    public ClearColor ClearValue { get; init; }
}

/// <summary>A depth (and optionally stencil) attachment. Reversed-Z clears depth to 0.</summary>
public readonly struct DepthAttachment
{
    /// <summary>The texture's backend handle; 0 = no depth attachment.</summary>
    public ulong TextureHandle { get; init; }

    /// <summary>Clear depth on load.</summary>
    public bool Clear { get; init; }

    /// <summary>The depth clear value when <see cref="Clear"/> is set.</summary>
    public float ClearDepth { get; init; }

    /// <summary>Read-only depth (test against a prepass; loaded, never written).</summary>
    public bool ReadOnly { get; init; }

    /// <summary>The attachment carries a stencil aspect (a combined depth-stencil format). When false, the stencil
    /// fields are ignored.</summary>
    public bool HasStencil { get; init; }

    /// <summary>Clear the stencil aspect to <see cref="ClearStencil"/> on load (else load it).</summary>
    public bool ClearStencilOnLoad { get; init; }

    /// <summary>The stencil clear value.</summary>
    public byte ClearStencil { get; init; }

    /// <summary>True when there is a depth attachment.</summary>
    public bool HasDepth => TextureHandle != 0;
}

/// <summary>The backend-agnostic graphics recording facade. The backend command list implements both
/// <see cref="ICommandList"/> (barriers and timestamps, graph-owned) and this (pass-owned draws). Binding allocates
/// nothing: value views, cached pipelines.</summary>
public interface IGraphicsRecorder
{
    /// <summary>Begins a render pass over the given attachments.</summary>
    /// <param name="colors">Color attachments.</param>
    /// <param name="depth">The depth attachment (may have no texture).</param>
    /// <param name="width">Render area width in pixels.</param>
    /// <param name="height">Render area height in pixels.</param>
    void BeginRendering(ReadOnlySpan<ColorAttachment> colors, in DepthAttachment depth, uint width, uint height);

    /// <summary>Ends the render pass.</summary>
    void EndRendering();

    /// <summary>Binds a graphics pipeline.</summary>
    /// <param name="pso">The pipeline.</param>
    void SetPipeline(IRenderPipeline pso);

    /// <summary>Sets the viewport and a matching scissor rectangle.</summary>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    /// <param name="minDepth">Minimum depth.</param>
    /// <param name="maxDepth">Maximum depth.</param>
    void SetViewportScissor(float x, float y, float width, float height, float minDepth = 0f, float maxDepth = 1f);

    /// <summary>Sets the scissor rectangle only (the viewport is unchanged): per-draw clip rectangles.</summary>
    /// <param name="x">Left.</param>
    /// <param name="y">Top.</param>
    /// <param name="width">Width.</param>
    /// <param name="height">Height.</param>
    void SetScissor(int x, int y, uint width, uint height);

    /// <summary>Sets the dynamic stencil reference value. A no-op for pipelines without stencil. The default is 0.</summary>
    /// <param name="reference">The reference value.</param>
    void SetStencilReference(uint reference);

    /// <summary>Binds a descriptor table to a root slot.</summary>
    /// <param name="rootSlot">The root slot.</param>
    /// <param name="range">The descriptor range.</param>
    void BindDescriptorTable(int rootSlot, DescriptorRange range);

    /// <summary>Binds a constant buffer.</summary>
    /// <param name="slot">The b-slot.</param>
    /// <param name="cbv">The buffer range.</param>
    void BindConstantBuffer(int slot, in GpuBufferView cbv);

    /// <summary>Binds a sampled image into texture slot <paramref name="slot"/>.</summary>
    /// <param name="slot">The t-slot.</param>
    /// <param name="srv">The texture view.</param>
    void BindTexture(int slot, ITextureView srv);

    /// <summary>Binds a sampler into sampler slot <paramref name="slot"/>.</summary>
    /// <param name="slot">The s-slot.</param>
    /// <param name="sampler">The sampler.</param>
    void BindSampler(int slot, ISampler sampler);

    /// <summary>Binds a structured or byte-address buffer as a shader resource (read) or unordered access (read-write).</summary>
    /// <param name="slot">The t- or u-slot.</param>
    /// <param name="buffer">The buffer range.</param>
    /// <param name="uav">True for unordered access.</param>
    void BindStorageBuffer(int slot, in GpuBufferView buffer, bool uav);

    /// <summary>Binds a vertex buffer.</summary>
    /// <param name="slot">The input slot.</param>
    /// <param name="vb">The buffer range.</param>
    /// <param name="stride">Vertex stride in bytes.</param>
    void BindVertexBuffer(int slot, in GpuBufferView vb, uint stride);

    /// <summary>Binds an index buffer.</summary>
    /// <param name="ib">The buffer range.</param>
    /// <param name="index32">True for 32-bit indices, false for 16-bit.</param>
    void BindIndexBuffer(in GpuBufferView ib, bool index32);

    /// <summary>Writes root / push constants.</summary>
    /// <typeparam name="T">The constant block type.</typeparam>
    /// <param name="data">The constants.</param>
    void PushConstants<T>(in T data)
        where T : unmanaged;

    /// <summary>Draws non-indexed primitives.</summary>
    /// <param name="vertexCount">Vertices per instance.</param>
    /// <param name="instanceCount">Instance count.</param>
    /// <param name="firstVertex">First vertex.</param>
    /// <param name="firstInstance">First instance.</param>
    void Draw(uint vertexCount, uint instanceCount, uint firstVertex, uint firstInstance);

    /// <summary>Draws indexed primitives.</summary>
    /// <param name="indexCount">Indices per instance.</param>
    /// <param name="instanceCount">Instance count.</param>
    /// <param name="firstIndex">First index.</param>
    /// <param name="vertexOffset">Value added to each index.</param>
    /// <param name="firstInstance">First instance.</param>
    void DrawIndexed(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance);

    /// <summary>GPU-driven indexed draw from an arguments buffer (optional indirect tier). One record is a
    /// VkDrawIndexedIndirectCommand / D3D12_DRAW_INDEXED_ARGUMENTS (20 bytes).</summary>
    /// <param name="args">The arguments buffer.</param>
    /// <param name="offsetBytes">Byte offset of the first record.</param>
    /// <param name="drawCount">Record count.</param>
    /// <param name="stride">Byte stride between records.</param>
    void DrawIndexedIndirect(in GpuBufferView args, uint offsetBytes, uint drawCount, uint stride);

    /// <summary>Opens a named GPU debug region on this recorder's timeline, paired with <see cref="EndEvent"/>. A no-op
    /// unless a marker backend is active and <see cref="Diagnostics.Profiler.GpuMarkers"/> is set.</summary>
    /// <param name="name">Region name.</param>
    /// <param name="color">ARGB color; 0 = automatic.</param>
    void BeginEvent(string name, uint color);

    /// <summary>Closes the most recent <see cref="BeginEvent"/> region.</summary>
    void EndEvent();

    /// <summary>Inserts an instantaneous named GPU marker.</summary>
    /// <param name="name">Marker name.</param>
    /// <param name="color">ARGB color; 0 = automatic.</param>
    void SetMarker(string name, uint color);
}
