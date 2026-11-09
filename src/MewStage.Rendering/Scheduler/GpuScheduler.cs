// The central GPU scheduler.
//
// ONE owner of the device's three queues, the shared shader-visible descriptor heaps, the transient pool, the
// timestamp query pool, the frame fences and the deferred-retirement ring. ALL GPU submission, render and compute,
// goes through it. Rendering and compute are TWO LOGICAL TIMELINES sharing one physical GPU: on a discrete GPU with a
// real compute queue, compute overlaps graphics, synchronized by a timeline value; on integrated or single-queue
// hardware (the baseline) compute folds into the graphics timeline as ordered dispatch segments. The two timelines
// stay distinct fence objects even when signalled from one hardware queue, so a compute readback wait is independent
// of the render present fence.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

using MewStage.Graphics;
using MewStage.Graphics.Diagnostics;
using MewStage.Rendering.Compute;
using MewStage.Rendering.Graph;

namespace MewStage.Rendering.Scheduler;

/// <summary>Handle to a submitted compute job's completion on the compute timeline.</summary>
public readonly struct SubmitToken
{
    /// <summary>Creates a token.</summary>
    /// <param name="fence">The compute timeline fence.</param>
    /// <param name="value">The value the job signals.</param>
    public SubmitToken(IFence fence, ulong value)
    {
        Fence = fence;
        Value = value;
    }

    /// <summary>The compute timeline fence.</summary>
    public IFence Fence { get; }

    /// <summary>The value the job signals.</summary>
    public ulong Value { get; }

    /// <summary>True once the job has completed.</summary>
    public bool IsComplete => Fence.CompletedValue >= Value;

    /// <summary>Blocks until the job has completed.</summary>
    public void Wait()
    {
        Fence.Wait(Value);
    }
}

/// <summary>A batch of compute passes submitted as ONE command list.</summary>
public sealed class ComputeJob
{
    /// <summary>Creates a job.</summary>
    /// <param name="name">Debug name.</param>
    public ComputeJob(string name)
    {
        Name = name;
    }

    /// <summary>Debug name.</summary>
    public string Name { get; }

    /// <summary>The passes, in record order.</summary>
    public List<IComputePass> Passes { get; } = [];

    /// <summary>Appends a pass.</summary>
    /// <param name="pass">The pass.</param>
    /// <returns>This job, for chaining.</returns>
    public ComputeJob Add(IComputePass pass)
    {
        Passes.Add(pass);
        return this;
    }

    /// <summary>Removes every pass.</summary>
    public void Reset()
    {
        Passes.Clear();
    }
}

/// <summary>Construction parameters for the scheduler.</summary>
public readonly struct GpuSchedulerOptions
{
    /// <summary>Frames in flight (at least 2).</summary>
    public int FramesInFlight { get; init; }

    /// <summary>Shader-visible CBV / SRV / UAV descriptor heap capacity, split evenly across the frames in flight.</summary>
    public int DescriptorHeapCapacity { get; init; }

    /// <summary>Shader-visible sampler heap capacity.</summary>
    public int SamplerHeapCapacity { get; init; }

    /// <summary>Upload ring bytes per frame.</summary>
    public ulong UploadRingBytesPerFrame { get; init; }

    /// <summary>Timestamp queries per frame.</summary>
    public int TimestampsPerFrame { get; init; }

    /// <summary>Present sync interval (1 = vsync).</summary>
    public int SyncInterval { get; init; }

    /// <summary>The defaults: 2 frames in flight, 4096 descriptors, 128 samplers, 8 MB of upload ring per frame, 64 timestamps, vsync.</summary>
    public static GpuSchedulerOptions Default => new()
    {
        FramesInFlight = 2,
        DescriptorHeapCapacity = 4096,
        SamplerHeapCapacity = 128,
        UploadRingBytesPerFrame = 8UL * 1024 * 1024,
        TimestampsPerFrame = 64,
        SyncInterval = 1,
    };
}

/// <summary>Owns the frame loop's GPU side: frames in flight, the render-graph compile and submit, compute jobs, present
/// and deferred destruction.</summary>
public sealed class GpuScheduler : IDisposable
{
    private readonly ISwapchain? _swapchain;
    private readonly FrameContext[] _frames;
    private readonly IDescriptorHeap _cbvSrvUavHeap;
    private readonly IDescriptorHeap _samplerHeap;
    private readonly IQueryHeap _timestampHeap;
    private readonly RenderGraphCompiler _compiler = new();
    private readonly IFence _computeFence;       // the compute LOGICAL timeline
    private readonly int _syncInterval;

    // Frame timing printed every 120 frames when MEWSTAGE_FRAME_STATS=1: where does a frame's wall time go? CPU blocked
    // on the throttle fence (GPU-bound), the render submit (CPU record-bound), or present (vsync / pacing-bound)?
    private readonly bool _frameStats = Environment.GetEnvironmentVariable("MEWSTAGE_FRAME_STATS") == "1";

    // Reusable single-element submit scratch (the render thread is single-threaded; the hot path allocates nothing).
    private readonly ICommandList[] _oneList = new ICommandList[1];
    private readonly FenceSignal[] _oneSignal = new FenceSignal[1];

    // Per-frame-in-flight command-list pools. Lists are reused per slot: the BeginFrame fence wait proves the slot's
    // prior submissions are GPU-complete, so Begin() can reset and refill. Cursors reset when a slot is re-entered.
    private readonly List<ICommandList>[] _gfxPool;
    private readonly int[] _gfxCursor;
    private readonly List<ICommandList>[] _computePool;
    private readonly int[] _computeCursor;

    // One compute recorder per pooled compute list, keyed by the list and re-targeted (nothing allocated per frame).
    private readonly Dictionary<ICommandList, IComputeRecorder> _computeRecorders = new(ReferenceEqualityComparer.Instance);

    private ulong _computeValue;
    private FrameContext? _current;
    private double _statWait;
    private double _statWaitMax;
    private double _statSubmit;
    private double _statSubmitMax;
    private double _statPresent;
    private double _statPresentMax;
    private int _statFrames;

    /// <summary>Creates the scheduler over a device and, for a windowed host, its swapchain.</summary>
    /// <param name="device">The device.</param>
    /// <param name="swapchain">The swapchain to present, or null for a headless scheduler.</param>
    /// <param name="options">Sizing; <see cref="GpuSchedulerOptions.Default"/> when null.</param>
    public GpuScheduler(IGraphicsDevice device, ISwapchain? swapchain = null, GpuSchedulerOptions? options = null)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device));
        _swapchain = swapchain;
        GpuSchedulerOptions o = options ?? GpuSchedulerOptions.Default;
        FramesInFlight = Math.Max(2, o.FramesInFlight);
        _syncInterval = o.SyncInterval;

        _cbvSrvUavHeap = device.CreateDescriptorHeap(DescriptorKind.CbvSrvUav, o.DescriptorHeapCapacity, shaderVisible: true);
        _samplerHeap = device.CreateDescriptorHeap(DescriptorKind.Sampler, o.SamplerHeapCapacity, shaderVisible: true);
        _timestampHeap = device.CreateTimestampQueryHeap(o.TimestampsPerFrame * FramesInFlight);
        _computeFence = device.CreateFence(0);

        _frames = new FrameContext[FramesInFlight];
        int descPerFrame = o.DescriptorHeapCapacity / FramesInFlight;
        for (int i = 0; i < FramesInFlight; i++)
        {
            IFence fence = device.CreateFence(0);
            UploadRing upload = new(device, o.UploadRingBytesPerFrame);
            TransientPool transient = new(device);
            DescriptorRange descRange = new(_cbvSrvUavHeap.Handle, i * descPerFrame, descPerFrame);
            DescriptorSubAllocator descAlloc = new(descRange);
            QueryRange queryRange = new(_timestampHeap, i * o.TimestampsPerFrame, o.TimestampsPerFrame);
            _frames[i] = new FrameContext(i, fence, upload, transient, descAlloc, queryRange);
        }

        _gfxPool = new List<ICommandList>[FramesInFlight];
        _computePool = new List<ICommandList>[FramesInFlight];
        _gfxCursor = new int[FramesInFlight];
        _computeCursor = new int[FramesInFlight];
        for (int i = 0; i < FramesInFlight; i++)
        {
            _gfxPool[i] = [];
            _computePool[i] = [];
        }
    }

    /// <summary>How many frames may be in flight on the GPU at once (the ring depth of the per-frame resources).</summary>
    public int FramesInFlight { get; }

    /// <summary>The device this scheduler submits to (fences, upload rings and descriptor budgets are its).</summary>
    public IGraphicsDevice Device { get; }

    /// <summary>The device's capabilities.</summary>
    public GpuCapabilities Caps => Device.Caps;

    /// <summary>The current frame number (monotonic). The in-flight slot is <c>FrameNumber % FramesInFlight</c>.</summary>
    public long FrameNumber { get; private set; }

    /// <summary>Acquires the next frame context: waits its fence (the throttle that bounds latency and memory; the CPU
    /// blocks only when the GPU is a full ring behind), resets its rings, rewinds its descriptor sub-range and frees
    /// what the fence proved idle.</summary>
    /// <returns>The frame context to record into.</returns>
    public FrameContext BeginFrame()
    {
        using ProfileScope zone = Profiler.BeginZone("GpuScheduler.BeginFrame");

        FrameContext ctx = _frames[FrameNumber % FramesInFlight];
        long t0 = _frameStats ? Stopwatch.GetTimestamp() : 0;
        ctx.FrameFence.Wait(ctx.FenceValue);   // the throttle
        if (_frameStats)
        {
            double ms = MsSince(t0);
            _statWait += ms;
            _statWaitMax = Math.Max(_statWaitMax, ms);
        }

        ctx.ResetForFrame();

        // Rewind only THIS in-flight frame's descriptor sub-range (its fence was just waited), never one shared region,
        // so frame N+1 can never overwrite descriptor tables that frame N's still-in-flight command list reads.
        int slot = (int)(FrameNumber % FramesInFlight);
        (Device as Backends.Vulkan.VulkanDevice)?.ResetFrameDescriptors(slot, FramesInFlight);
        (Device as Backends.Dx12.Dx12Device)?.ResetFrameDescriptors(slot, FramesInFlight);

        // Advance and reclaim the device-level deferred-destroy queue: free resources deferred at least FramesInFlight
        // frames ago (their slot fence is now proven idle), and stamp anything deferred this frame with _frameNo.
        Device.RetireDeferred(FrameNumber, FramesInFlight);
        FlushPendingDeletes(ctx);
        _gfxCursor[slot] = 0;
        _computeCursor[slot] = 0;   // the slot fence was waited: its pooled lists are reusable
        _current = ctx;
        return ctx;
    }

    /// <summary>Compiles, records and submits a render graph into the current frame context.</summary>
    /// <param name="graph">The graph built this frame.</param>
    /// <param name="ctx">The frame context from <see cref="BeginFrame"/>.</param>
    /// <returns>The compiled graph (for inspection).</returns>
    public CompiledRenderGraph Submit(RenderGraph graph, FrameContext ctx)
    {
        using ProfileScope zone = Profiler.BeginZone("GpuScheduler.Submit");

        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(ctx);
        long t0 = _frameStats ? Stopwatch.GetTimestamp() : 0;
        CompiledRenderGraph compiled = _compiler.Compile(graph);
        ctx.Transient.Populate(compiled);                    // allocate and bind the transient slot backings
        ICommandList cmd = AcquirePooled(_gfxPool, _gfxCursor, Device.GraphicsQueue);
        cmd.Begin();
        cmd.BeginEvent("Frame", EventColor.Frame);           // the top-level GPU region enclosing every pass
        compiled.Execute(cmd, (ulong)FrameNumber, ComputeRecorderFor(ctx, cmd));
        cmd.EndEvent();
        cmd.Close();
        _oneList[0] = cmd;

        // The frame's render submit: the ONLY submit that carries the swapchain's acquire / present sync (Vulkan).
        Device.GraphicsQueue.SubmitFrame(_oneList, default, default);
        if (_frameStats)
        {
            double ms = MsSince(t0);
            _statSubmit += ms;
            _statSubmitMax = Math.Max(_statSubmitMax, ms);
        }

        return compiled;
    }

    /// <summary>Submits a compute job on the compute LOGICAL timeline. With async compute it overlaps graphics on the
    /// real compute queue; on the single-queue baseline it folds into the graphics queue, ordered, but always signals the
    /// distinct compute fence so a CPU readback wait is independent of the render present fence.</summary>
    /// <param name="job">The job.</param>
    /// <param name="recorder">A recorder to use instead of the backend's (tests pass an inspectable one), or null.</param>
    /// <returns>A token for the job's completion.</returns>
    public SubmitToken SubmitCompute(ComputeJob job, IComputeRecorder? recorder = null)
    {
        using ProfileScope zone = Profiler.BeginZone("GpuScheduler.SubmitCompute");

        ArgumentNullException.ThrowIfNull(job);
        ICommandQueue queue = Caps.HasAsyncCompute ? Device.ComputeQueue : Device.GraphicsQueue;
        ICommandList cmd = AcquirePooled(_computePool, _computeCursor, queue);
        cmd.Begin();

        // Every pass goes into ONE command list (a barrier-free dispatch chain the driver can overlap).
        IComputeRecorder rec = recorder ?? PooledComputeRecorder(cmd);
        ComputeFrameContext fctx = new() { FrameIndex = (ulong)FrameNumber, Mode = Caps.ComputeSubmission };
        cmd.BeginEvent(job.Name, EventColor.Render);
        foreach (IComputePass pass in job.Passes)
        {
            pass.Record(rec, in fctx);
        }

        cmd.EndEvent();
        cmd.Close();
        ulong value = ++_computeValue;
        _oneList[0] = cmd;
        _oneSignal[0] = new FenceSignal(_computeFence, value);
        queue.Submit(_oneList, default, _oneSignal);
        return new SubmitToken(_computeFence, value);
    }

    /// <summary>Presents, signals the frame fence (so the context can be recycled a ring later) and advances the frame.</summary>
    /// <param name="ctx">The frame context from <see cref="BeginFrame"/>.</param>
    public void EndFrame(FrameContext ctx)
    {
        using ProfileScope zone = Profiler.BeginZone("GpuScheduler.EndFrame");

        ArgumentNullException.ThrowIfNull(ctx);
        long t0 = _frameStats ? Stopwatch.GetTimestamp() : 0;
        Device.GraphicsQueue.SetMarker("Present", EventColor.Frame);
        _swapchain?.Present(_syncInterval);
        if (_frameStats)
        {
            ReportFrameStats(MsSince(t0));
        }

        ulong v = ctx.FenceValue + 1;
        ctx.FenceValue = v;
        _oneSignal[0] = new FenceSignal(ctx.FrameFence, v);
        Device.GraphicsQueue.Submit(default, default, _oneSignal);
        _current = null;
        FrameNumber++;
    }

    /// <summary>Allocates descriptors from the current frame's sub-range.</summary>
    /// <param name="count">Descriptor count.</param>
    /// <returns>The range.</returns>
    public DescriptorRange AllocateDescriptors(int count)
    {
        return (_current ?? throw new InvalidOperationException("AllocateDescriptors outside BeginFrame/EndFrame.")).Descriptors.Allocate(count);
    }

    /// <summary>Reserves a timestamp slot from the current frame's query range.</summary>
    /// <returns>The absolute query index, or -1 when exhausted.</returns>
    public int AllocateTimestamp()
    {
        return (_current ?? throw new InvalidOperationException("AllocateTimestamp outside BeginFrame/EndFrame.")).Timestamps.Next();
    }

    /// <summary>Queues a resource for destruction once the current frame's fence signals.</summary>
    /// <param name="resource">The resource.</param>
    public void Retire(IGpuResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        (_current ?? _frames[FrameNumber % FramesInFlight]).PendingDeletes.Add(resource);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Drain the GPU before teardown so in-flight command lists and allocators are idle (D3D12 has no implicit device
        // wait), then dispose the pooled lists deterministically.
        for (int i = 0; i < _frames.Length; i++)
        {
            try
            {
                _frames[i].FrameFence.Wait(_frames[i].FenceValue);
            }
            catch (Exception)
            {
                // A lost device cannot be waited on; teardown continues.
            }
        }

        for (int i = 0; i < FramesInFlight; i++)
        {
            foreach (ICommandList l in _gfxPool[i])
            {
                l.Dispose();
            }

            foreach (ICommandList l in _computePool[i])
            {
                l.Dispose();
            }
        }

        for (int i = 0; i < _frames.Length; i++)
        {
            _frames[i].Dispose();
        }

        _cbvSrvUavHeap.Dispose();
        _samplerHeap.Dispose();
        _timestampHeap.Dispose();
        _computeFence.Dispose();
    }

    private static double MsSince(long t0)
    {
        return (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
    }

    private static ICommandList AddToPool(List<ICommandList> pool, ICommandQueue queue)
    {
        ICommandList list = queue.AcquireCommandList();
        pool.Add(list);
        return list;
    }

    private static void FlushPendingDeletes(FrameContext ctx)
    {
        // The slot's fence was just waited in BeginFrame, so every resource retired while this slot was last active (a
        // ring ago) is GPU-idle now: dispose it directly.
        List<IGpuResource> list = ctx.PendingDeletes;
        for (int i = 0; i < list.Count; i++)
        {
            list[i].Dispose();
        }

        list.Clear();
    }

    private void ReportFrameStats(double presentMs)
    {
        _statPresent += presentMs;
        _statPresentMax = Math.Max(_statPresentMax, presentMs);
        if (++_statFrames < 120)
        {
            return;
        }

        Console.Error.WriteLine(string.Format(
            CultureInfo.InvariantCulture,
            "[frame-stats] {0}f avg/max ms: fenceWait {1:F2}/{2:F2}  submit {3:F2}/{4:F2}  present {5:F2}/{6:F2}",
            _statFrames, _statWait / _statFrames, _statWaitMax, _statSubmit / _statFrames, _statSubmitMax, _statPresent / _statFrames, _statPresentMax));
        _statWait = _statWaitMax = _statSubmit = _statSubmitMax = _statPresent = _statPresentMax = 0;
        _statFrames = 0;
    }

    // One compute recorder per frame slot, re-targeted to the frame's list.
    private IComputeRecorder ComputeRecorderFor(FrameContext ctx, ICommandList cmd)
    {
        if (ctx.ComputeRecorder is IRetargetableComputeRecorder r)
        {
            if (!ReferenceEquals(r.CommandList, cmd))
            {
                r.Retarget(cmd);
            }

            return r;
        }

        IComputeRecorder rec = Device.CreateComputeRecorder(cmd);
        if (rec is IRetargetableComputeRecorder rr)
        {
            rr.Retarget(cmd);
        }

        ctx.ComputeRecorder = rec;
        return rec;
    }

    private IComputeRecorder PooledComputeRecorder(ICommandList cmd)
    {
        if (_computeRecorders.TryGetValue(cmd, out IComputeRecorder? rec))
        {
            if (rec is IRetargetableComputeRecorder r)
            {
                r.Retarget(cmd);   // also resets the per-recording timestamp slot
            }

            return rec;
        }

        rec = Device.CreateComputeRecorder(cmd);
        _computeRecorders[cmd] = rec;
        return rec;
    }

    // Reuse a pooled command list for the current frame slot, or grow the pool on first use of a slot. Safe because
    // BeginFrame waited the slot's fence (prior submissions GPU-complete) and reset its cursor.
    private ICommandList AcquirePooled(List<ICommandList>[] pool, int[] cursor, ICommandQueue queue)
    {
        int slot = (int)(FrameNumber % FramesInFlight);
        List<ICommandList> p = pool[slot];
        int c = cursor[slot];
        ICommandList list = c < p.Count ? p[c] : AddToPool(p, queue);
        cursor[slot] = c + 1;
        return list;
    }
}
