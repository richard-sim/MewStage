// The render-graph compiler, the compiled graph and its executor.
//
// Compile (bake) steps:
//   1. Validate declarations.
//   2. Dependency DAG + reachability from the graph outputs through producers; PASS CULLING of anything unreachable
//      falls out for free.
//   3. Order: stable declaration order over the surviving passes (the correct topological baseline).
//   4. Logical -> physical resource assignment.
//   5. Lifetime + transient aliasing: resources with identical (format, dims, samples) and non-overlapping pass
//      ranges share one backing allocation; History resources never alias.
//   6. Render-pass merge groups (a hint the backend may honor on tile-based GPUs).
//   7. Barrier synthesis: per PHYSICAL SLOT, deduplicated, with a terminal reset of every transient to Common.
//
// The compiler allocates nothing in steady state (reused scratch) and produces an inspectable plan for headless unit
// tests (no GPU needed).

using System;
using System.Collections.Generic;

using MewStage.Graphics;
using MewStage.Graphics.Diagnostics;

namespace MewStage.Rendering.Graph;

/// <summary>A compiled, ready-to-submit pass: ordered, with its synthesized barriers and execute delegate.</summary>
public sealed class CompiledPass
{
    /// <summary>Pass name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>Pass kind.</summary>
    public PassKind Kind { get; internal set; }

    /// <summary>Timestamp pair slot, or -1 when timestamps were not assigned.</summary>
    public int TimestampSlot { get; internal set; } = -1;

    /// <summary>Merge group id: adjacent graphics passes sharing attachments share a group (a subpass fusion hint).</summary>
    public int MergeGroup { get; internal set; } = -1;

    /// <summary>The barriers issued before the pass. Their handles are LOGICAL resource indices, resolved at execute time.</summary>
    public IReadOnlyList<ResourceBarrierDesc> BarriersView => Barriers;

    internal RgExecute? Execute { get; set; }

    internal List<ResourceBarrierDesc> Barriers { get; } = [];

    internal void Reset()
    {
        Name = "";
        Kind = PassKind.Graphics;
        TimestampSlot = -1;
        MergeGroup = -1;
        Execute = null;
        Barriers.Clear();
    }
}

/// <summary>The baked graph: ordered passes (culled ones removed) with synthesized barriers, the transient-resource plan
/// (slot count, descriptions, aliasing) and an executor. The frame context allocates transient backing for each slot
/// and binds it before <see cref="Execute(ICommandList, ulong)"/>.</summary>
public sealed class CompiledRenderGraph
{
    // CompiledPass records are pooled across compiles (0 B per steady-state frame). The compiled graph is one reused
    // object per compiler, so a pass from a previous compile was never valid after the next one anyway.
    private readonly List<CompiledPass> _passPool = [];
    private readonly List<CompiledPass> _passes = [];

    // Logical resource index -> physical: either an imported handle or a transient slot.
    private bool[] _isImported = [];
    private ulong[] _importedHandle = [];
    private int[] _slotOf = [];           // transient slot index, or -1 if imported
    private ulong[] _slotHandle = [];     // bound backend handle per transient slot
    private RgResourceKind[] _slotKind = [];
    private RgTextureDesc[] _slotTex = [];
    private RgBufferDesc[] _slotBuf = [];
    private ResourceBarrierDesc[] _barrierScratch = new ResourceBarrierDesc[16]; // reused; no per-frame allocation

    /// <summary>The surviving passes in execution order.</summary>
    public IReadOnlyList<CompiledPass> Passes => _passes;

    /// <summary>Number of distinct transient backing allocations after aliasing.</summary>
    public int TransientSlotCount { get; private set; }

    /// <summary>Number of timestamp pairs assigned (one per surviving pass when requested).</summary>
    public int TimestampCount { get; internal set; }

    /// <summary>The kind of resource a transient slot holds.</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The kind.</returns>
    public RgResourceKind SlotKind(int slot)
    {
        return _slotKind[slot];
    }

    /// <summary>The texture description of a transient slot (valid when <see cref="SlotKind"/> is a texture).</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The description.</returns>
    public RgTextureDesc SlotTexture(int slot)
    {
        return _slotTex[slot];
    }

    /// <summary>The buffer description of a transient slot (valid when <see cref="SlotKind"/> is a buffer).</summary>
    /// <param name="slot">The slot.</param>
    /// <returns>The description.</returns>
    public RgBufferDesc SlotBuffer(int slot)
    {
        return _slotBuf[slot];
    }

    /// <summary>Binds the allocated backend handle for a transient slot. The frame context calls it before <see cref="Execute(ICommandList, ulong)"/>.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="backendHandle">The backend handle of the backing resource.</param>
    public void BindTransientSlot(int slot, ulong backendHandle)
    {
        _slotHandle[slot] = backendHandle;
    }

    /// <summary>Resolves a logical resource index to its bound backend handle (the imported handle, or the aliased slot's handle).</summary>
    /// <param name="logicalIndex">The logical resource index.</param>
    /// <returns>The backend handle, or 0 when nothing is bound.</returns>
    public ulong ResolvePhysical(int logicalIndex)
    {
        if (_isImported[logicalIndex])
        {
            return _importedHandle[logicalIndex];
        }

        int slot = _slotOf[logicalIndex];
        return slot >= 0 ? _slotHandle[slot] : 0UL;
    }

    /// <summary>The transient slot a logical resource was assigned, or -1 when it is imported. For tests and inspection.</summary>
    /// <param name="logicalIndex">The logical resource index.</param>
    /// <returns>The slot, or -1.</returns>
    public int SlotOf(int logicalIndex)
    {
        return _isImported[logicalIndex] ? -1 : _slotOf[logicalIndex];
    }

    /// <summary>Records every pass in order: issues its barriers, then invokes its execute delegate.</summary>
    /// <param name="cmd">The open command list.</param>
    /// <param name="frameIndex">The frame being recorded.</param>
    public void Execute(ICommandList cmd, ulong frameIndex)
    {
        Execute(cmd, frameIndex, null);
    }

    /// <summary>As <see cref="Execute(ICommandList, ulong)"/>, with the frame slot's compute recorder handed to every pass
    /// (<see cref="RgPassContext.Compute"/>).</summary>
    /// <param name="cmd">The open command list.</param>
    /// <param name="frameIndex">The frame being recorded.</param>
    /// <param name="compute">The compute recorder over <paramref name="cmd"/>, or null.</param>
    public void Execute(ICommandList cmd, ulong frameIndex, IComputeRecorder? compute)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        foreach (CompiledPass p in _passes)
        {
            // Per-pass GPU marker region (D3D12 event / Vulkan debug-utils label). Encloses the pass's barriers and draws
            // so a capture attributes them to the pass. A no-op unless GPU markers are enabled.
            cmd.BeginEvent(p.Name, EventColor.Render);
            if (p.Barriers.Count > 0)
            {
                if (_barrierScratch.Length < p.Barriers.Count)
                {
                    Array.Resize(ref _barrierScratch, p.Barriers.Count);
                }

                Span<ResourceBarrierDesc> span = _barrierScratch.AsSpan(0, p.Barriers.Count);
                for (int i = 0; i < p.Barriers.Count; i++)
                {
                    ResourceBarrierDesc b = p.Barriers[i];

                    // The barrier handle stores the LOGICAL resource index at compile time; resolve it to the bound
                    // physical handle (imported handle or aliased slot handle) for this frame.
                    ulong phys = ResolvePhysical((int)b.ResourceHandle);
                    span[i] = b.IsUavBarrier
                        ? ResourceBarrierDesc.Uav(phys)
                        : ResourceBarrierDesc.Transition(phys, b.Before, b.After);
                }

                cmd.ResourceBarrier(span);
            }

            // The timestamp pair around the pass is issued by the frame context (it owns the query heap); TimestampSlot
            // is the metadata it uses.
            p.Execute?.Invoke(new RgPassContext(this, cmd, frameIndex, compute));
            cmd.EndEvent();
        }
    }

    internal void ResetForCompile(int resourceCount)
    {
        _passes.Clear();
        if (_isImported.Length < resourceCount)
        {
            _isImported = new bool[resourceCount];
            _importedHandle = new ulong[resourceCount];
            _slotOf = new int[resourceCount];
        }

        Array.Clear(_isImported, 0, resourceCount);
        Array.Clear(_importedHandle, 0, resourceCount);
        for (int i = 0; i < resourceCount; i++)
        {
            _slotOf[i] = -1;
        }

        TransientSlotCount = 0;
        TimestampCount = 0;
    }

    internal CompiledPass AddCompiledPass()
    {
        CompiledPass p;
        if (_passes.Count < _passPool.Count)
        {
            p = _passPool[_passes.Count];
            p.Reset();
        }
        else
        {
            p = new CompiledPass();
            _passPool.Add(p);
        }

        _passes.Add(p);
        return p;
    }

    internal void SetImported(int logical, ulong handle)
    {
        _isImported[logical] = true;
        _importedHandle[logical] = handle;
    }

    internal int AddSlot(RgResourceKind kind, in RgTextureDesc t, in RgBufferDesc b)
    {
        if (_slotKind.Length == TransientSlotCount)
        {
            Array.Resize(ref _slotKind, Math.Max(8, TransientSlotCount * 2));
            Array.Resize(ref _slotTex, _slotKind.Length);
            Array.Resize(ref _slotBuf, _slotKind.Length);
            Array.Resize(ref _slotHandle, _slotKind.Length);
        }

        _slotKind[TransientSlotCount] = kind;
        _slotTex[TransientSlotCount] = t;
        _slotBuf[TransientSlotCount] = b;
        _slotHandle[TransientSlotCount] = 0;
        return TransientSlotCount++;
    }

    internal void AssignSlot(int logical, int slot)
    {
        _slotOf[logical] = slot;
    }
}

/// <summary>Compiles a <see cref="RenderGraph"/> into a <see cref="CompiledRenderGraph"/>. Reusable: it holds its scratch.</summary>
public sealed class RenderGraphCompiler
{
    private readonly CompiledRenderGraph _out = new();
    private readonly Queue<int> _bfs = new();
    private readonly List<int> _survivingPassIds = [];
    private readonly List<int> _order = [];
    private readonly Dictionary<AliasKey, List<(int Slot, int FreeAfter)>> _freeByKey = [];
    private readonly Comparison<int> _byFirstUse;

    // Per-logical-resource state (imported resources only use it; transients are tracked per slot below).
    private ResourceState[] _curState = [];

    // Per-PHYSICAL-SLOT state for step 7: N logical transients can share one backing, and a GPU resource has exactly
    // one state.
    private ResourceState[] _slotState = [];
    private bool[] _reachable = [];
    private IReadOnlyList<RgResource> _sortResources = [];

    /// <summary>Creates a compiler.</summary>
    public RenderGraphCompiler()
    {
        _byFirstUse = ByFirstUse;
    }

    private readonly record struct AliasKey(int Kind, Format Format, uint Width, uint Height, uint SamplesAndLayers, ulong SizeBytes, uint Stride);

    /// <summary>Compiles the graph.</summary>
    /// <param name="graph">The graph built this frame.</param>
    /// <param name="assignTimestamps">Assign a timestamp pair per surviving pass.</param>
    /// <returns>The compiled graph (one reused instance per compiler).</returns>
    public CompiledRenderGraph Compile(RenderGraph graph, bool assignTimestamps = true)
    {
        ArgumentNullException.ThrowIfNull(graph);
        IReadOnlyList<RgResource> resources = graph.ResourcesInternal;
        IReadOnlyList<RgPass> passes = graph.PassesInternal;
        int resourceCount = graph.ResourceCount;
        int passCount = graph.PassCount;

        Validate(resources, passes, passCount);
        bool[] reachable = CullUnreachable(resources, passes, passCount);

        // ---- 3. Order: stable declaration order over surviving passes; mark resource usage and lifetimes. ----
        _out.ResetForCompile(resourceCount);
        MarkLifetimes(resources, passes, passCount, reachable);

        // ---- 4/5. Physical assignment + transient aliasing ----
        AssignSlots(resources, resourceCount);

        // ---- 7. Barrier synthesis over surviving passes ----
        int timestamps = SynthesizeBarriers(resources, passes, resourceCount, assignTimestamps);
        ResetTransientsToCommon(resources, resourceCount);

        if (assignTimestamps)
        {
            _out.TimestampCount = timestamps;
        }

        return _out;
    }

    // Same comparison as List.Sort with a Comparison: same introsort, same order for equal keys.
    private int ByFirstUse(int x, int y)
    {
        return _sortResources[x].FirstUse.CompareTo(_sortResources[y].FirstUse);
    }

    private static bool Writes(RgPass pass, int resourceIndex)
    {
        foreach (RgAccess a in pass.Accesses)
        {
            if (a.IsWrite && a.ResourceIndex == resourceIndex)
            {
                return true;
            }
        }

        return false;
    }

    // ---- 1. Validate: every read must be satisfied by an import or an earlier-or-same producer. ----
    private static void Validate(IReadOnlyList<RgResource> resources, IReadOnlyList<RgPass> passes, int passCount)
    {
        for (int pi = 0; pi < passCount; pi++)
        {
            RgPass p = passes[pi];
            foreach (RgAccess a in p.Accesses)
            {
                if (a.IsWrite || resources[a.ResourceIndex].Imported)
                {
                    continue;
                }

                bool produced = false;
                for (int qi = 0; qi <= pi && !produced; qi++)
                {
                    produced = Writes(passes[qi], a.ResourceIndex);
                }

                if (!produced)
                {
                    throw new InvalidOperationException($"Pass '{p.Name}' reads resource #{a.ResourceIndex} that no prior pass produces and which is not imported.");
                }
            }
        }
    }

    // ---- 2. Dependency DAG + reachability culling from graph outputs. A pass is reachable if it (transitively)
    // produces a resource consumed on the path to a graph-output write: breadth-first backwards from the passes that
    // write an output or imported resource, through the nearest prior writer of everything each pass touches. ----
    private bool[] CullUnreachable(IReadOnlyList<RgResource> resources, IReadOnlyList<RgPass> passes, int passCount)
    {
        if (_reachable.Length < passCount)
        {
            _reachable = new bool[Math.Max(passCount, 32)];
        }

        bool[] reachable = _reachable;
        Array.Clear(reachable, 0, passCount);
        _bfs.Clear();
        for (int pi = 0; pi < passCount; pi++)
        {
            foreach (RgAccess a in passes[pi].Accesses)
            {
                if (a.IsWrite && (resources[a.ResourceIndex].IsGraphOutput || resources[a.ResourceIndex].Imported))
                {
                    reachable[pi] = true;
                    _bfs.Enqueue(pi);
                    break;
                }
            }
        }

        // If nothing writes an output or an import, the LAST declared pass is the root (still useful for tests).
        if (_bfs.Count == 0 && passCount > 0)
        {
            reachable[passCount - 1] = true;
            _bfs.Enqueue(passCount - 1);
        }

        while (_bfs.Count > 0)
        {
            int pi = _bfs.Dequeue();
            foreach (RgAccess a in passes[pi].Accesses)
            {
                for (int qi = pi - 1; qi >= 0; qi--)
                {
                    if (!Writes(passes[qi], a.ResourceIndex))
                    {
                        continue;
                    }

                    if (!reachable[qi])
                    {
                        reachable[qi] = true;
                        _bfs.Enqueue(qi);
                    }

                    break;
                }
            }
        }

        return reachable;
    }

    private void MarkLifetimes(IReadOnlyList<RgResource> resources, IReadOnlyList<RgPass> passes, int passCount, bool[] reachable)
    {
        int survivingIndex = 0;
        _survivingPassIds.Clear();
        for (int pi = 0; pi < passCount; pi++)
        {
            if (!reachable[pi])
            {
                continue;
            }

            foreach (RgAccess a in passes[pi].Accesses)
            {
                RgResource r = resources[a.ResourceIndex];
                r.Used = true;
                r.FirstUse = Math.Min(r.FirstUse, survivingIndex);
                r.LastUse = Math.Max(r.LastUse, survivingIndex);
            }

            _survivingPassIds.Add(pi);
            survivingIndex++;
        }
    }

    // Imported -> its imported handle. Transient (used, non-History) -> greedy slot reuse over non-overlapping lifetimes
    // within identical alias keys. History -> its own dedicated slot (never aliased). Resources are processed in
    // first-use order for good packing.
    private void AssignSlots(IReadOnlyList<RgResource> resources, int resourceCount)
    {
        foreach (List<(int Slot, int FreeAfter)> pool in _freeByKey.Values)
        {
            pool.Clear();   // keep the per-shape lists (bounded by the number of distinct shapes)
        }

        _order.Clear();
        for (int i = 0; i < resourceCount; i++)
        {
            if (resources[i].Used)
            {
                _order.Add(i);
            }
        }

        _sortResources = resources;
        _order.Sort(_byFirstUse);
        _sortResources = [];

        foreach (int ri in _order)
        {
            RgResource r = resources[ri];
            if (r.Imported)
            {
                _out.SetImported(ri, r.ImportedHandle);
                continue;
            }

            if (r.History)
            {
                _out.AssignSlot(ri, _out.AddSlot(r.Kind, r.Tex, r.Buf));
                continue;
            }

            AliasKey key = r.Kind == RgResourceKind.Texture
                ? new AliasKey(0, r.Tex.Format, r.Tex.Width, r.Tex.Height, (Math.Max(1u, r.Tex.SampleCount) * 1000) + Math.Max(1u, r.Tex.ArrayLayers), 0UL, 0u)
                : new AliasKey(1, Format.Unknown, 0u, 0u, 0u, r.Buf.SizeBytes, r.Buf.StructureStride);

            if (!_freeByKey.TryGetValue(key, out List<(int Slot, int FreeAfter)>? pool))
            {
                pool = [];
                _freeByKey[key] = pool;
            }

            int chosen = -1;
            for (int k = 0; k < pool.Count; k++)
            {
                if (pool[k].FreeAfter < r.FirstUse)
                {
                    // The slot is free strictly before this resource's first use.
                    chosen = pool[k].Slot;
                    pool[k] = (chosen, r.LastUse);
                    break;
                }
            }

            if (chosen < 0)
            {
                chosen = _out.AddSlot(r.Kind, r.Tex, r.Buf);
                pool.Add((chosen, r.LastUse));
            }

            _out.AssignSlot(ri, chosen);
        }
    }

    // STATE IS TRACKED PER PHYSICAL SLOT, NOT PER LOGICAL RESOURCE. Steps 4/5 let two logical transients with
    // non-overlapping lifetimes SHARE one backing slot. A GPU resource has ONE state, so the second occupant's first
    // barrier must transition from whatever the FIRST occupant left the slot in. Per-logical tracking would initialize
    // every transient to Common and emit `Common -> X` against a slot that is really in X'. On D3D12 that is a
    // ResourceBarrierBeforeAfterMismatch and the resource is left inconsistent; Vulkan tolerates it (a wrong oldLayout
    // on a discardable image), which is why it shows up as a D3D12-only failure. Tracking per slot makes any multi-pass
    // code that reuses a same-shape transient within one frame correct by construction; the render-graph tests pin it.
    //
    // No D3D12 ALIASING barrier is needed alongside it: the transient pool rents whole textures / buffers per slot and
    // reuses them by shape, so two logical resources sharing a slot share one committed resource, not two overlapping
    // placed resources in one heap. If the pool ever becomes a real heap sub-allocator, an aliasing barrier belongs
    // here, at the first access of a slot's second occupant.
    private int SynthesizeBarriers(IReadOnlyList<RgResource> resources, IReadOnlyList<RgPass> passes, int resourceCount, bool assignTimestamps)
    {
        if (_curState.Length < resourceCount)
        {
            _curState = new ResourceState[resourceCount];
        }

        int slotCount = _out.TransientSlotCount;
        if (_slotState.Length < slotCount)
        {
            _slotState = new ResourceState[Math.Max(slotCount, 16)];
        }

        for (int i = 0; i < slotCount; i++)
        {
            _slotState[i] = ResourceState.Common;
        }

        for (int i = 0; i < resourceCount; i++)
        {
            _curState[i] = resources[i].Imported ? resources[i].ImportedInitialState : ResourceState.Common;
        }

        int ts = 0;
        foreach (int pi in _survivingPassIds)
        {
            RgPass src = passes[pi];
            CompiledPass cp = _out.AddCompiledPass();
            cp.Name = src.Name;
            cp.Kind = src.Kind;
            cp.Execute = src.Execute;
            if (assignTimestamps)
            {
                cp.TimestampSlot = ts++;
            }

            // Per resource accessed by this pass, emit a barrier from the backing's current state to the required one.
            foreach (RgAccess a in src.Accesses)
            {
                int ri = a.ResourceIndex;
                ResourceState cur = StateOf(ri);
                ResourceState req = a.State;
                if (cur == req)
                {
                    // Same state across consecutive passes is STILL a hazard for WRITE states: two passes that both write
                    // the same render target / depth (the world pass, then the UI pass loading over it) are NOT
                    // auto-synchronized across dynamic-rendering pass boundaries on Vulkan, and dependent UAV dispatches
                    // need read-write ordering. Emit a self-barrier (a flush). D3D12 orders same-state render-target /
                    // depth writes within a command list (its backend skips before == after transitions), so this is
                    // effectively Vulkan-only; there the self-transition becomes a real execution + memory barrier.
                    if (req == ResourceState.UnorderedAccess)
                    {
                        cp.Barriers.Add(ResourceBarrierDesc.Uav((ulong)ri));
                    }
                    else if (req is ResourceState.RenderTarget or ResourceState.DepthWrite or ResourceState.CopyDest)
                    {
                        cp.Barriers.Add(ResourceBarrierDesc.Transition((ulong)ri, req, req));
                    }

                    continue;
                }

                // The handle is the LOGICAL index; it is resolved to the bound physical handle at execute time.
                cp.Barriers.Add(ResourceBarrierDesc.Transition((ulong)ri, cur, req));
                SetState(ri, req);
            }
        }

        return ts;
    }

    // The graph-output / imported resources must end in their required terminal state; the backbuffer is transitioned
    // to Present by an explicit final pass the host adds, which keeps the compiler policy-free.
    //
    // TRANSIENT residual-state reset (the D3D12 ResourceBarrierBeforeAfterMismatch fix). Transient backings are RENTED
    // from a per-frame-slot pool and REUSED across frames by shape; their GPU state PERSISTS between frames, but each
    // compile re-initializes every transient to Common. So if a transient ends a frame in a non-Common state (a depth
    // stencil left in DepthWrite), the SAME backing re-rented N frames later has the real state DepthWrite while the new
    // compile synthesises a Common -> DepthWrite barrier: the Before mismatches the resource's actual state. Restore the
    // invariant the next compile assumes: a terminal Transition(cur -> Common) on the LAST surviving pass for every used
    // transient still in a non-Common state. ONE reset per SLOT, not per logical resource: N aliased resources share one
    // backing, so N transitions would mean N-1 with a stale before-state. Imported resources are excluded (their terminal
    // state is owned by the explicit present / terminal pass). This costs a handful of barriers on the final pass and is
    // allocation-neutral (barriers reuse the pass's list).
    private void ResetTransientsToCommon(IReadOnlyList<RgResource> resources, int resourceCount)
    {
        if (_survivingPassIds.Count == 0)
        {
            return;
        }

        CompiledPass lastPass = _out.Passes[_out.Passes.Count - 1];
        for (int ri = 0; ri < resourceCount; ri++)
        {
            if (!resources[ri].Used || resources[ri].Imported)
            {
                continue;
            }

            int slot = _out.SlotOf(ri);
            if (slot < 0 || _slotState[slot] == ResourceState.Common)
            {
                continue;
            }

            lastPass.Barriers.Add(ResourceBarrierDesc.Transition((ulong)ri, _slotState[slot], ResourceState.Common));
            _slotState[slot] = ResourceState.Common;
        }
    }

    // Read / write the state of whatever BACKING a logical resource is bound to: the imported handle's own tracked
    // state, or the shared slot's.
    private ResourceState StateOf(int ri)
    {
        int slot = _out.SlotOf(ri);
        return slot < 0 ? _curState[ri] : _slotState[slot];
    }

    private void SetState(int ri, ResourceState state)
    {
        int slot = _out.SlotOf(ri);
        if (slot < 0)
        {
            _curState[ri] = state;
        }
        else
        {
            _slotState[slot] = state;
        }
    }
}
