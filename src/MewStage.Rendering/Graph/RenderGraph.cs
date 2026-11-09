// The render graph (frame graph) model and its per-frame builder.
//
// The frame is expressed as a render graph REBUILT EACH FRAME: passes declare logical resources and their reads and
// writes; the compiler (RenderGraphCompiler) bakes a correct submission (barriers, transient-resource aliasing, pass
// culling, per-pass timing) so feature code never writes barriers by hand and conditional passes drop in and out
// trivially. The graph is small, so the per-frame bake is cheap.
//
// Allocation rule: the builder reuses its backing storage across frames (Reset() clears counts and keeps capacity),
// each pass record reuses its PassBuilder, and passes supply CACHED execute delegates (no per-frame closures; a setup
// with per-frame values uses AddPass<TState> with a static lambda). A steady-state frame through Reset + AddPass +
// RenderGraphCompiler.Compile + Execute allocates 0 B (the render-graph allocation test). RgTextureDesc.History = true
// marks resources that must never be aliased (anything read back the next frame).

using System;
using System.Collections.Generic;

using MewStage.Graphics;
using MewStage.Graphics.Diagnostics;

namespace MewStage.Rendering.Graph;

/// <summary>Opaque handle to a logical render-graph resource (index + generation for reuse-safety).</summary>
public readonly struct RgHandle : IEquatable<RgHandle>
{
    internal RgHandle(int index, int generation)
    {
        Index = index;
        Generation = generation;
    }

    /// <summary>The logical resource index within this frame's graph.</summary>
    public int Index { get; }

    /// <summary>The graph generation the handle was issued in; a handle from an earlier frame is rejected.</summary>
    public int Generation { get; }

    /// <summary>True when the handle was issued by a graph (the default value is invalid).</summary>
    public bool IsValid => Generation != 0;

    /// <summary>Value equality.</summary>
    public static bool operator ==(RgHandle left, RgHandle right)
    {
        return left.Equals(right);
    }

    /// <summary>Value inequality.</summary>
    public static bool operator !=(RgHandle left, RgHandle right)
    {
        return !left.Equals(right);
    }

    /// <inheritdoc />
    public bool Equals(RgHandle other)
    {
        return Index == other.Index && Generation == other.Generation;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is RgHandle h && Equals(h);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(Index, Generation);
    }
}

/// <summary>Logical texture description for a graph resource. Transient unless imported.</summary>
public readonly record struct RgTextureDesc
{
    /// <summary>Debug name.</summary>
    public string Name { get; init; }

    /// <summary>Texel format.</summary>
    public Format Format { get; init; }

    /// <summary>Width in texels.</summary>
    public uint Width { get; init; }

    /// <summary>Height in texels.</summary>
    public uint Height { get; init; }

    /// <summary>Multisample count (0 or 1 = none).</summary>
    public uint SampleCount { get; init; }

    /// <summary>Mip level count (0 = 1).</summary>
    public uint MipLevels { get; init; }

    /// <summary>Array layer count (0 = 1).</summary>
    public uint ArrayLayers { get; init; }

    /// <summary>True: never aliased (a resource whose contents must survive into the next frame).</summary>
    public bool History { get; init; }
    /// <summary>The constant color this target is cleared to each frame, if any (TextureDesc.ClearColor: the D3D12
    /// optimized clear value). Part of the transient pool's key, not of the alias key: aliasing stays shape-based
    /// and a slot shared by targets with different clear colors only loses the fast-clear hint.</summary>
    public ClearColor? ClearColor { get; init; }

    /// <summary>Aliasing key: resources with identical (format, dims, samples, layers) and non-overlapping lifetimes may share backing.</summary>
    public (Format Format, uint Width, uint Height, uint Samples, uint Layers) AliasKey => (Format, Width, Height, Math.Max(1u, SampleCount), Math.Max(1u, ArrayLayers));
}

/// <summary>Logical buffer description for a graph resource.</summary>
public readonly record struct RgBufferDesc
{
    /// <summary>Debug name.</summary>
    public string Name { get; init; }

    /// <summary>Size in bytes.</summary>
    public ulong SizeBytes { get; init; }

    /// <summary>Element stride for structured buffers (0 for raw).</summary>
    public uint StructureStride { get; init; }

    /// <summary>True: never aliased.</summary>
    public bool History { get; init; }

    /// <summary>Aliasing key: buffers of identical size and stride with non-overlapping lifetimes may share backing.</summary>
    public (ulong SizeBytes, uint Stride) AliasKey => (SizeBytes, StructureStride);
}

/// <summary>What a graph resource is.</summary>
public enum RgResourceKind : byte
{
    /// <summary>A texture.</summary>
    Texture = 0,

    /// <summary>A buffer.</summary>
    Buffer = 1,
}

/// <summary>Internal resource record (one per logical resource declared this frame).</summary>
internal sealed class RgResource
{
    public int Index;
    public RgResourceKind Kind;
    public RgTextureDesc Tex;
    public RgBufferDesc Buf;
    public bool Imported;
    public ulong ImportedHandle;          // backend resource handle when imported (e.g. backbuffer)
    public ResourceState ImportedInitialState;
    public bool IsGraphOutput;            // e.g. the backbuffer/present target — culling root
    public bool History => Kind == RgResourceKind.Texture ? Tex.History : Buf.History;

    // Filled by the compiler:
    public int FirstUse = int.MaxValue;
    public int LastUse = -1;
    public int PhysicalSlot = -1;
    public bool Used;

    public void Reset()
    {
        FirstUse = int.MaxValue;
        LastUse = -1;
        PhysicalSlot = -1;
        Used = false;
        Imported = false;
        IsGraphOutput = false;
        ImportedHandle = 0;
        ImportedInitialState = ResourceState.Common;
    }
}

/// <summary>A single resource access by a pass (read or write) with its required state + shader-stage mask.</summary>
internal readonly struct RgAccess
{
    public readonly int ResourceIndex;
    public readonly ResourceState State;
    public readonly ShaderStage Stage;
    public readonly bool IsWrite;
    public RgAccess(int res, ResourceState state, ShaderStage stage, bool isWrite)
    {
        ResourceIndex = res;
        State = state;
        Stage = stage;
        IsWrite = isWrite;
    }
}

/// <summary>A pass's cached execute delegate. No per-frame closures.</summary>
public delegate void RgExecute(in RgPassContext ctx);

/// <summary>A pass setup that takes its per-frame values as an argument (RenderGraph.AddPass with a state): pass a
/// <c>static</c> lambda so no closure is allocated per frame.</summary>
public delegate void PassSetup<TState>(PassBuilder builder, in TState state);

/// <summary>Internal pass record.</summary>
internal sealed class RgPass
{
    public int Index;
    public string Name = "";
    public PassKind Kind;
    public readonly List<RgAccess> Accesses = [];
    public RgExecute? Execute;
    public bool Culled;
    public int TimestampSlot = -1;
    /// <summary>The pass record's builder, created once with the record and reused every frame (a steady-state
    /// frame through the graph allocates nothing; a fresh PassBuilder per AddPass was 32 B per pass per frame).</summary>
    public PassBuilder? Builder;

    public void Reset()
    {
        Name = "";
        Kind = PassKind.Graphics;
        Accesses.Clear();
        Execute = null;
        Culled = false;
        TimestampSlot = -1;
    }
}

/// <summary>
/// Setup-time builder handed to each pass's setup callback. Declares the pass's resource
/// reads/writes and its cached execute delegate. Allocations come from the graph's reused storage. One builder per
/// pass RECORD, reused every frame: it is valid only inside the setup call (do not keep it).
/// </summary>
public sealed class PassBuilder
{
    private readonly RenderGraph _graph;
    private readonly RgPass _pass;
    internal PassBuilder(RenderGraph graph, RgPass pass)
    {
        _graph = graph;
        _pass = pass;
    }

    /// <summary>Declare a read of an existing resource at the given shader stage.</summary>
    public void Read(RgHandle h, ShaderStage stage = ShaderStage.Pixel, ResourceState state = ResourceState.ShaderResource)
    {
        _pass.Accesses.Add(new RgAccess(_graph.Validate(h), state, stage, isWrite: false));
    }

    /// <summary>Declare a read of a texture as an SRV (convenience).</summary>
    public void ReadTexture(RgHandle h, ShaderStage stage)
    {
        Read(h, stage, ResourceState.ShaderResource);
    }

    /// <summary>Declare a read of a buffer (SRV/CBV).</summary>
    public void ReadBuffer(RgHandle h, ShaderStage stage, ResourceState state = ResourceState.ShaderResource)
    {
        Read(h, stage, state);
    }

    /// <summary>Declare a write to an existing resource.</summary>
    public void Write(RgHandle h, ResourceState state, ShaderStage stage = ShaderStage.Pixel)
    {
        _pass.Accesses.Add(new RgAccess(_graph.Validate(h), state, stage, isWrite: true));
    }

    /// <summary>Create a transient color render target written by this pass; returns its handle.</summary>
    public RgHandle CreateColor(in RgTextureDesc desc)
    {
        RgHandle h = _graph.CreateTexture(desc);
        _pass.Accesses.Add(new RgAccess(h.Index, ResourceState.RenderTarget, ShaderStage.Pixel, isWrite: true));
        return h;
    }

    /// <summary>Create a transient depth target written by this pass; returns its handle.</summary>
    public RgHandle CreateDepth(in RgTextureDesc desc)
    {
        RgHandle h = _graph.CreateTexture(desc);
        _pass.Accesses.Add(new RgAccess(h.Index, ResourceState.DepthWrite, ShaderStage.Pixel, isWrite: true));
        return h;
    }

    /// <summary>Create a transient UAV buffer written by this pass (compute output).</summary>
    public RgHandle CreateBuffer(in RgBufferDesc desc)
    {
        RgHandle h = _graph.CreateBuffer(desc);
        _pass.Accesses.Add(new RgAccess(h.Index, ResourceState.UnorderedAccess, ShaderStage.Compute, isWrite: true));
        return h;
    }

    /// <summary>Write an existing target as a render target (e.g. composite into the backbuffer).</summary>
    public void WriteColor(RgHandle h)
    {
        Write(h, ResourceState.RenderTarget, ShaderStage.Pixel);
    }

    /// <summary>Bind the cached execute delegate (no per-frame closure — GC rule,).</summary>
    public void SetExecute(RgExecute execute)
    {
        _pass.Execute = execute ?? throw new ArgumentNullException(nameof(execute));
    }
}

/// <summary>
/// The per-frame render graph. Reused across frames via <see cref="Reset"/> (keeps capacity; GC rule).
/// Declare imports, then <c>AddPass</c> for each pass in intended execution order, then hand to
/// <see cref="RenderGraphCompiler"/>.
/// </summary>
public sealed class RenderGraph
{
    private readonly List<RgResource> _resources = [];
    private readonly List<RgPass> _passes = [];
    private int _generation = 1;

    internal IReadOnlyList<RgResource> ResourcesInternal => _resources;
    internal IReadOnlyList<RgPass> PassesInternal => _passes;
    internal int ResourceCount { get; private set; }
    internal int PassCount { get; private set; }

    /// <summary>Clear for a new frame, retaining backing capacity (no GC). Bumps generation to invalidate stale handles.</summary>
    public void Reset()
    {
        using ProfileScope zone = Profiler.BeginZone("RenderGraph.Reset");

        for (int i = 0; i < ResourceCount; i++)
        {
            _resources[i].Reset();
        }

        for (int i = 0; i < PassCount; i++)
        {
            _passes[i].Reset();
        }

        ResourceCount = 0;
        PassCount = 0;
        unchecked
        {
            _generation++;
            if (_generation == 0)
            {
                _generation = 1;
            }
        }
    }

    private RgResource NextResource()
    {
        if (ResourceCount == _resources.Count)
        {
            _resources.Add(new RgResource());
        }

        RgResource r = _resources[ResourceCount];
        r.Index = ResourceCount;
        ResourceCount++;
        return r;
    }

    private RgPass NextPass()
    {
        if (PassCount == _passes.Count)
        {
            _passes.Add(new RgPass());
        }

        RgPass p = _passes[PassCount];
        p.Index = PassCount;
        PassCount++;
        return p;
    }

    /// <summary>Import an externally-owned texture (e.g. the swapchain backbuffer) with its current state.</summary>
    public RgHandle ImportTexture(in RgTextureDesc desc, ulong backendHandle, ResourceState initialState, bool isGraphOutput = false)
    {
        RgResource r = NextResource();
        r.Kind = RgResourceKind.Texture;
        r.Tex = desc;
        r.Imported = true;
        r.ImportedHandle = backendHandle;
        r.ImportedInitialState = initialState;
        r.IsGraphOutput = isGraphOutput;
        return new RgHandle(r.Index, _generation);
    }

    /// <summary>Import an externally-owned buffer (persistent input).</summary>
    public RgHandle ImportBuffer(in RgBufferDesc desc, ulong backendHandle, ResourceState initialState)
    {
        RgResource r = NextResource();
        r.Kind = RgResourceKind.Buffer;
        r.Buf = desc;
        r.Imported = true;
        r.ImportedHandle = backendHandle;
        r.ImportedInitialState = initialState;
        return new RgHandle(r.Index, _generation);
    }

    internal RgHandle CreateTexture(in RgTextureDesc desc)
    {
        RgResource r = NextResource();
        r.Kind = RgResourceKind.Texture;
        r.Tex = desc;
        return new RgHandle(r.Index, _generation);
    }

    internal RgHandle CreateBuffer(in RgBufferDesc desc)
    {
        RgResource r = NextResource();
        r.Kind = RgResourceKind.Buffer;
        r.Buf = desc;
        return new RgHandle(r.Index, _generation);
    }

    /// <summary>Add a pass; the setup callback declares its resources and execute delegate.</summary>
    public void AddPass(string name, PassKind kind, Action<PassBuilder> setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        RgPass p = NextPass();
        p.Name = name;
        p.Kind = kind;
        setup(p.Builder ??= new PassBuilder(this, p));
    }

    /// <summary>Add a pass whose setup reads per-pass values from <paramref name="state"/> instead of capturing them:
    /// with a <c>static</c> lambda and a struct state the call allocates nothing (no closure per frame, no boxing).
    /// The builder is valid only during the call (it is the pass record's reused builder).</summary>
    public void AddPass<TState>(string name, PassKind kind, in TState state, PassSetup<TState> setup)
    {
        ArgumentNullException.ThrowIfNull(setup);
        RgPass p = NextPass();
        p.Name = name;
        p.Kind = kind;
        setup(p.Builder ??= new PassBuilder(this, p), in state);
    }

    internal int Validate(RgHandle h)
    {
        return h.Generation != _generation
            ? throw new InvalidOperationException("Stale RgHandle from a previous frame (call after Reset).")
            : (uint)h.Index >= (uint)ResourceCount ? throw new ArgumentOutOfRangeException(nameof(h)) : h.Index;
    }
}

/// <summary>
/// Execution-time context passed to each pass's <see cref="RgExecute"/> delegate. Resolves logical handles
/// to backend resource handles and exposes the recording command list. Populated by the compiled graph's
/// executor (which the GpuScheduler/frame-context drives).
/// </summary>
public readonly ref struct RgPassContext
{
    private readonly CompiledRenderGraph _compiled;
    /// <summary>The command list being recorded.</summary>
    public ICommandList CommandList { get; }
    /// <summary>The frame being recorded.</summary>
    public ulong FrameIndex { get; }
    /// <summary>The frame slot's compute recorder over <see cref="CommandList"/> (created once per frame context and
    /// re-targeted per frame). Null when the executor was given none (headless graph tests).</summary>
    public IComputeRecorder? Compute { get; }

    internal RgPassContext(CompiledRenderGraph compiled, ICommandList cmd, ulong frameIndex, IComputeRecorder? compute)
    {
        _compiled = compiled;
        CommandList = cmd;
        FrameIndex = frameIndex;
        Compute = compute;
    }

    /// <summary>Resolve a logical handle to the backend resource handle bound for this frame (physical, post-aliasing).</summary>
    public ulong Resolve(RgHandle h)
    {
        return _compiled.ResolvePhysical(h.Index);
    }

    /// <summary>The graphics recorder for this pass (the backend command list implements it). Pass-owned draws.</summary>
    public IGraphicsRecorder Recorder => (IGraphicsRecorder)CommandList;
}
