// DX12 IGraphicsDevice backend (Vortice.Windows) — MewStage.Rendering.
// Windows primary backend. Compiled cross-platform (Vortice managed assemblies are RID-agnostic);
// RUNTIME is Windows-only (P/Invoke into d3d12.dll / dxgi.dll); it compiles everywhere. Full device surface: queues, committed resources, descriptor
// heaps (RTV/DSV/shader-visible), root signatures, graphics+compute PSOs, resource-state barriers, ID3D12Fence
// timeline, timestamp queries, deferred destruction, device-loss (DXGI_ERROR_DEVICE_REMOVED). DX12 has no
// dynamic rendering — the recorder maps BeginRendering→OMSetRenderTargets + ClearRTV/DSV.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using MewStage.Graphics;
using MewStage.Graphics.Diagnostics;

using Vortice.Direct3D;

using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

using D3D = Vortice.Direct3D12;
using DescriptorRange = MewStage.Graphics.DescriptorRange;
using DXGI = Vortice.DXGI;
using Format = MewStage.Graphics.Format;

namespace MewStage.Rendering.Backends.Dx12;

internal abstract class D12Resource : IGpuResource
{
    public ulong Handle { get; }
    protected readonly Dx12Device Dev;
    private bool _disposed;
    protected D12Resource(Dx12Device dev)
    {
        Dev = dev;
        Handle = dev.NextHandle();
        dev.Register(this);
    }
    /// <summary>Submit serial of the last command list that used this resource (lifetime validation; 0 = never).</summary>
    internal long LastUseSerial;
    protected abstract void Destroy();
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Dev.Lifetime.CheckDispose(LastUseSerial, "DX12 resource #" + Handle.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _disposed = true;
        Destroy();
        Dev.Unregister(Handle);
    }
}

internal sealed unsafe class D12Buffer : D12Resource, IBuffer, IMappedBuffer
{
    public ID3D12Resource Resource;
    public ulong SizeBytes { get; }
    private readonly MemoryClass _mem; MemoryClass IBuffer.Memory => _mem;

    public IntPtr MappedPointer { get; }
    public D12Buffer(Dx12Device dev, in BufferDesc d) : base(dev)
    {
        SizeBytes = d.SizeBytes;
        _mem = d.Memory;
        HeapType heap = Dx12Map.HeapType(d.Memory);
        ResourceStates init = heap == HeapType.Upload ? ResourceStates.GenericRead : heap == HeapType.Readback ? ResourceStates.CopyDest : ResourceStates.Common;
        // With the poison switch a DEFAULT-heap buffer is created CREATE_NOT_ZEROED (the zero fill would mask an
        // unwritten read) and filled on the GPU; upload / readback buffers are filled through their mapping.
        bool poison = dev.PoisonNewAllocations;
        HeapFlags flags = poison && heap == HeapType.Default ? HeapFlags.CreateNotZeroed : HeapFlags.None;
        Resource = dev.Device.CreateCommittedResource(new HeapProperties(heap), flags,
            ResourceDescription.Buffer(d.SizeBytes, Dx12Map.ResourceFlags(d.Usage)), init);
        if (heap is HeapType.Upload or HeapType.Readback)
        {
            void* pp = null;
            _ = Resource.Map(0u, &pp);
            MappedPointer = (IntPtr)pp;
            if (poison)
            {
                GpuPoison.Fill(pp, d.SizeBytes, dev.PoisonFill!.Value);
            }
        }
        else if (poison)
        {
            dev.PoisonBuffer(Resource, d.SizeBytes);
        }

        if (!string.IsNullOrEmpty(d.DebugName))
        {
            try
            {
                Resource.Name = d.DebugName;
            }
            catch { /* DRED naming is best-effort */ }
        }
    }
    protected override void Destroy()
    {
        if (MappedPointer != IntPtr.Zero)
        {
            Resource.Unmap(0u);
        }

        Resource.Dispose();
    }
}

internal sealed class D12Texture : D12Resource, ITexture
{
    public ID3D12Resource Resource;
    public TextureDesc Desc { get; }
    public readonly bool DepthSrv;
    public D12Texture(Dx12Device dev, in TextureDesc d) : base(dev)
    {
        Desc = d;
        // A depth texture that is ALSO a shader resource must be a TYPELESS resource on DX12 (DSV=D32_FLOAT,
        // SRV=R32_FLOAT). A plain D32_FLOAT resource cannot be SRV'd — the null-desc SRV then samples an
        // invalid/null descriptor => GPU page fault @ VA 0 => device removed (a later pass samples depth).
        DepthSrv = (d.Usage & ResourceUsage.DepthStencil) != 0 && (d.Usage & ResourceUsage.ShaderResource) != 0 && Dx12Map.IsDepth(d.Format);
        DXGI.Format resFmt = DepthSrv ? DXGI.Format.R32_Typeless : Dx12Map.Format(d.Format);
        var rd = ResourceDescription.Texture2D(resFmt, d.Width, d.Height,
            (ushort)Math.Max(1u, d.DepthOrArrayLayers), (ushort)Math.Max(1u, d.MipLevels), Math.Max(1u, d.SampleCount), 0, Dx12Map.ResourceFlags(d.Usage));
        // Optimized clear value (a fast clear and no Clear*ViewMismatchingClearValue warning from the debug layer):
        //  - depth-stencil textures: the engine's reversed-Z clear (depth=0, stencil=0), ALSO for the typeless
        //    DepthSrv resource -- the clear value carries the fully-typed DSV format (D32_FLOAT), which D3D12 accepts
        //    on an R32_TYPELESS resource (the format must be castable from the resource format; MiniEngine's
        //    DepthBuffer does the same). The earlier "typeless may not carry one" note was never measured; if the
        //    runtime does reject it, the catch below recreates without it and says so once on the console.
        //  - render targets: the constant color the engine clears them to, when TextureDesc.ClearColor is set
        //    (fp-hdr, fp-ao, the post-process targets, the motion-vector transient); none otherwise.
        ClearValue? optClear = null;
        if ((d.Usage & ResourceUsage.DepthStencil) != 0)
        {
            optClear = new ClearValue(Dx12Map.Format(d.Format), 0.0f, 0);
        }
        else if ((d.Usage & ResourceUsage.RenderTarget) != 0 && d.ClearColor is { } cc)
        {
            optClear = new ClearValue(Dx12Map.Format(d.Format), new Vortice.Mathematics.Color4(cc.R, cc.G, cc.B, cc.A));
        }
        // The poison switch creates the texture CREATE_NOT_ZEROED and fills it on the GPU (Dx12Device.PoisonTexture).
        HeapFlags heapFlags = dev.PoisonNewAllocations ? HeapFlags.CreateNotZeroed : HeapFlags.None;
        try
        {
            Resource = dev.Device.CreateCommittedResource(new HeapProperties(HeapType.Default), heapFlags, rd, ResourceStates.Common, optClear);
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (optClear is not null)
        {
            Console.WriteLine($"DX12: optimized clear value rejected for '{d.DebugName}' ({ex.Message}); creating without one (the Clear*ViewMismatchingClearValue perf warning returns for it).");
            Resource = dev.Device.CreateCommittedResource(new HeapProperties(HeapType.Default), heapFlags, rd, ResourceStates.Common, null);
        }

        if (dev.PoisonNewAllocations)
        {
            dev.PoisonTexture(Resource, d, rd, DepthSrv);
        }

        if (!string.IsNullOrEmpty(d.DebugName))
        {
            try
            {
                Resource.Name = d.DebugName;
            }
            catch { /* DRED naming is best-effort */ }
        }
    }
    public D12Texture(Dx12Device dev, ID3D12Resource external, in TextureDesc d) : base(dev)
    {
        Desc = d;
        Resource = external;
    }
    protected override void Destroy()
    {
        Resource.Dispose();
    }
}

internal sealed class D12BufferView : D12Resource, IBufferView
{
    public GpuBufferView View { get; }
    public D12BufferView(Dx12Device dev, IBuffer b, in BufferViewDesc d) : base(dev)
    {
        View = new GpuBufferView(b.Handle, d.Offset, d.SizeBytes == 0 ? b.SizeBytes : d.SizeBytes);
    }

    protected override void Destroy() { }
}
internal sealed class D12TextureView : D12Resource, ITextureView
{
    public readonly D12Texture Texture; public readonly TextureViewDesc Desc;
    public D12TextureView(Dx12Device dev, D12Texture tex, in TextureViewDesc d) : base(dev)
    {
        Texture = tex;
        Desc = d;
    }
    protected override void Destroy() { }
}
internal sealed class D12Sampler : D12Resource, ISampler { public readonly SamplerDesc Desc; public D12Sampler(Dx12Device dev, in SamplerDesc d) : base(dev) { Desc = d; } protected override void Destroy() { } }

internal sealed class D12Fence : D12Resource, IFence
{
    public ID3D12Fence Fence;
    public D12Fence(Dx12Device dev, ulong initial) : base(dev)
    {
        Fence = dev.Device.CreateFence(initial, FenceFlags.None);
    }

    public ulong CompletedValue => Fence.CompletedValue;
    /// <summary>Block the calling thread until the fence reaches <paramref name="value"/> (previously a
    /// Thread.SpinWait busy-loop that burned a core for the whole GPU wait). Event-based: register a per-THREAD
    /// auto-reset event with SetEventOnCompletion and sleep on it, re-checking CompletedValue in a loop, so a stale signal from an
    /// earlier timed-out registration can only cause one extra re-check, never an early return. Returns under the
    /// SAME condition as before (CompletedValue >= value), so frame ordering is unchanged. Device removal makes
    /// CompletedValue UINT64_MAX, which ends the loop. The 10 s TimeoutException is kept.</summary>
    public void Wait(ulong value)
    {
        if (Fence.CompletedValue >= value)
        {
            NoteWaited(value);
            return;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        nint evt = Dx12FenceEvent.ForCurrentThread();
        while (Fence.CompletedValue < value)
        {
            double elapsedMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            if (elapsedMs >= WaitTimeoutMs)
            {
                throw new TimeoutException("fence wait");
            }

            _ = Fence.SetEventOnCompletion(value, evt);
            Dx12FenceEvent.Wait(evt, (uint)Math.Ceiling(WaitTimeoutMs - elapsedMs));
        }

        NoteWaited(value);
    }
    private const double WaitTimeoutMs = 10_000.0;   // unchanged from the spin version (10 s)
    // Lifetime validation: the value + submit serial of the last signal queued on this fence.
    private ulong _signalValue;
    private long _signalSerial;
    internal void NoteSignal(ulong value, long serial)
    {
        if (serial > 0 && value >= _signalValue)
        {
            _signalValue = value;
            _signalSerial = serial;
        }
    }
    internal void NoteWaited(ulong value)
    {
        if (value >= _signalValue)
        {
            Dev.Lifetime.OnCompleted(_signalSerial);
        }
    }
    protected override void Destroy()
    {
        Fence.Dispose();
    }
}

/// <summary>Per-thread Win32 auto-reset event for <see cref="D12Fence.Wait"/>. One event per waiting thread (not
/// per fence), so two threads waiting on one fence never share an event. Created on first use and kept for the
/// thread's lifetime (a handful of threads ever wait on a fence).</summary>
internal static partial class Dx12FenceEvent
{
    [ThreadStatic]
    private static nint s_event;   // one Win32 event per waiting thread

    public static nint ForCurrentThread()
    {
        if (s_event == 0)
        {
            s_event = CreateEventW(0, 0, 0, 0);
            if (s_event == 0)
            {
                throw new InvalidOperationException("CreateEventW failed for the DX12 fence wait");
            }
        }

        return s_event;
    }

    public static void Wait(nint evt, uint timeoutMs)
    {
        _ = WaitForSingleObject(evt, timeoutMs);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateEventW(nint attributes, int manualReset, int initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);
}

internal sealed class D12QueryHeap : D12Resource, IQueryHeap
{
    public ID3D12QueryHeap Heap;
    public int RangeStart => 0;
    public int Capacity { get; }
    public D12QueryHeap(Dx12Device dev, int capacity) : base(dev)
    {
        Capacity = capacity;
        Heap = dev.Device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, (uint)capacity));
    }
    public void ResolveTo(IBuffer dst) { /* ResolveQueryData recorded on the command list at frame end */ }
    protected override void Destroy()
    {
        Heap.Dispose();
    }
}

internal sealed class D12DescriptorHeap : D12Resource, IDescriptorHeap
{
    public ID3D12DescriptorHeap Heap;
    public DescriptorKind Kind { get; }
    private int _used; private readonly int _cap;
    public D12DescriptorHeap(Dx12Device dev, DescriptorKind kind, int capacity, bool shaderVisible) : base(dev)
    {
        Kind = kind;
        _cap = capacity;
        DescriptorHeapType type = kind == DescriptorKind.Sampler ? DescriptorHeapType.Sampler : DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
        Heap = dev.Device.CreateDescriptorHeap(new DescriptorHeapDescription(type, (uint)capacity, shaderVisible ? DescriptorHeapFlags.ShaderVisible : DescriptorHeapFlags.None));
    }
    public DescriptorRange Allocate(int count)
    {
        int s = _used;
        _used += count;
        return _used > _cap ? throw new InvalidOperationException("DX12 descriptor heap exhausted.") : new DescriptorRange(Handle, s, count);
    }
    protected override void Destroy()
    {
        Heap.Dispose();
    }
}

internal sealed class D12PipelineLayout : D12Resource, IPipelineLayout
{
    public ID3D12RootSignature RootSignature;
    // Root-parameter indices: one CBV/SRV/UAV table, one sampler table, root constants.
    public readonly int TableRootIndex = -1, SamplerRootIndex = -1, RootConstantsIndex = -1;
    public readonly int CbvCount, SrvCount, UavCount, TexSrvCount, SamplerCount, RootConstants32;
    public D12PipelineLayout(Dx12Device dev, in PipelineLayoutDesc d) : base(dev)
    {
        CbvCount = d.CbvCount;
        SrvCount = d.SrvCount;
        UavCount = d.UavCount;
        TexSrvCount = d.TextureSrvCount;
        SamplerCount = d.SamplerCount;
        RootConstants32 = d.RootConstants32;
        var ranges = new List<DescriptorRange1>();
        if (d.CbvCount > 0)
        {
            ranges.Add(new DescriptorRange1(DescriptorRangeType.ConstantBufferView, (uint)d.CbvCount, 0u, 0u, uint.MaxValue, DescriptorRangeFlags.DescriptorsVolatile | DescriptorRangeFlags.DataVolatile));
        }
        // buffer SRVs (t0..) then sampled-image SRVs (t{SrvCount}..) share the t-register space, one contiguous range.
        int totalSrv = d.SrvCount + d.TextureSrvCount;
        if (totalSrv > 0)
        {
            ranges.Add(new DescriptorRange1(DescriptorRangeType.ShaderResourceView, (uint)totalSrv, 0u, 0u, uint.MaxValue, DescriptorRangeFlags.DescriptorsVolatile | DescriptorRangeFlags.DataVolatile));
        }

        if (d.UavCount > 0)
        {
            ranges.Add(new DescriptorRange1(DescriptorRangeType.UnorderedAccessView, (uint)d.UavCount, 0u, 0u, uint.MaxValue, DescriptorRangeFlags.DescriptorsVolatile | DescriptorRangeFlags.DataVolatile));
        }

        var rootParams = new List<RootParameter1>();
        if (ranges.Count > 0)
        {
            TableRootIndex = rootParams.Count;
            rootParams.Add(new RootParameter1(new RootDescriptorTable1([.. ranges]), ShaderVisibility.All));
        }

        if (d.SamplerCount > 0)
        {
            SamplerRootIndex = rootParams.Count;
            rootParams.Add(new RootParameter1(new RootDescriptorTable1([new DescriptorRange1(DescriptorRangeType.Sampler, (uint)d.SamplerCount, 0u, 0u, uint.MaxValue, DescriptorRangeFlags.DescriptorsVolatile)]), ShaderVisibility.All));
        }

        if (d.RootConstants32 > 0)
        {
            RootConstantsIndex = rootParams.Count;
            rootParams.Add(new RootParameter1(new RootConstants((uint)d.CbvCount, 0u, (uint)d.RootConstants32), ShaderVisibility.All));
        }

        var desc = new RootSignatureDescription1(RootSignatureFlags.AllowInputAssemblerInputLayout, [.. rootParams]);
        RootSignature = dev.Device.CreateRootSignature(new VersionedRootSignatureDescription(desc));
    }
    protected override void Destroy()
    {
        RootSignature.Dispose();
    }
}

internal sealed class D12ComputePipeline : D12Resource, IComputePipeline
{
    public ID3D12PipelineState Pso; public string Name { get; }
    public readonly D12PipelineLayout LayoutObj;
    public D12ComputePipeline(Dx12Device dev, in ComputePipelineDesc d) : base(dev)
    {
        Name = d.Name;
        LayoutObj = (D12PipelineLayout)d.Layout;
        var desc = new ComputePipelineStateDescription { RootSignature = ((D12PipelineLayout)d.Layout).RootSignature, ComputeShader = d.ByteCode };
        Pso = dev.Device.CreateComputePipelineState(desc);
    }
    protected override void Destroy()
    {
        Pso.Dispose();
    }
}
internal sealed class D12RenderPipeline : D12Resource, IRenderPipeline
{
    public ID3D12PipelineState? Pso; public string Name { get; }
    public readonly D12PipelineLayout LayoutObj;
    public readonly Vortice.Direct3D.PrimitiveTopology IaTopology;
    public D12RenderPipeline(Dx12Device dev, in RenderPipelineDesc d) : base(dev)
    {
        Name = d.Name;
        LayoutObj = (D12PipelineLayout)d.Layout;
        IaTopology = d.Topology switch { Graphics.PrimitiveTopology.LineList => Vortice.Direct3D.PrimitiveTopology.LineList, Graphics.PrimitiveTopology.TriangleStrip => Vortice.Direct3D.PrimitiveTopology.TriangleStrip, Graphics.PrimitiveTopology.PointList => Vortice.Direct3D.PrimitiveTopology.PointList, _ => Vortice.Direct3D.PrimitiveTopology.TriangleList };
        VertexAttribute[] attrs = d.VertexAttributes ?? [];
        var elems = new InputElementDescription[attrs.Length];
        for (int i = 0; i < attrs.Length; i++)
        {
            elems[i] = new InputElementDescription(attrs[i].Semantic ?? "TEXCOORD", (uint)(attrs[i].Semantic is null ? attrs[i].Location : attrs[i].SemanticIndex), Dx12Map.Format(attrs[i].Format), (uint)attrs[i].Offset, 0);
        }

        var rtFormats = new DXGI.Format[d.ColorFormats?.Length ?? 0];
        for (int i = 0; i < rtFormats.Length; i++)
        {
            rtFormats[i] = Dx12Map.Format(d.ColorFormats![i]);
        }

        RasterizerDescription rast = RasterizerDescription.CullCounterClockwise;   // CCW front
        rast.CullMode = d.Cull switch { Graphics.CullMode.Front => D3D.CullMode.Front, Graphics.CullMode.None => D3D.CullMode.None, _ => D3D.CullMode.Back };
        rast.FrontCounterClockwise = true;
        if (d.Wireframe)
        {
            rast.FillMode = FillMode.Wireframe;
        }

        var depth = new DepthStencilDescription { DepthEnable = d.DepthTest, DepthWriteMask = d.DepthWrite ? DepthWriteMask.All : DepthWriteMask.Zero, DepthFunc = d.DepthCompare switch { DepthCompare.GreaterEqual => ComparisonFunction.GreaterEqual, DepthCompare.Equal => ComparisonFunction.Equal, DepthCompare.LessEqual => ComparisonFunction.LessEqual, DepthCompare.Always => ComparisonFunction.Always, _ => ComparisonFunction.Greater } }; // reversed-Z default
        // Stencil-then-cover (NanoVG concave UI fills). Off by default (StencilEnable = false leaves it untouched).
        if (d.StencilEnable)
        {
            depth.StencilEnable = true;
            depth.StencilReadMask = d.StencilReadMask;
            depth.StencilWriteMask = d.StencilWriteMask;
            depth.FrontFace = Dx12StencilFace(d.StencilFront);
            depth.BackFace = Dx12StencilFace(d.StencilBack);
        }

        var desc = new GraphicsPipelineStateDescription
        {
            RootSignature = ((D12PipelineLayout)d.Layout).RootSignature,
            VertexShader = d.VertexShader,
            PixelShader = (d.PixelShader != null && d.PixelShader.Length > 0) ? d.PixelShader : default,
            InputLayout = new InputLayoutDescription(elems),
            RasterizerState = rast,
            DepthStencilState = depth,
            BlendState = Dx12BlendState(d.Blend, d.ColorWriteDisable),
            PrimitiveTopologyType = d.Topology switch { Graphics.PrimitiveTopology.LineList => PrimitiveTopologyType.Line, Graphics.PrimitiveTopology.PointList => PrimitiveTopologyType.Point, _ => PrimitiveTopologyType.Triangle },
            SampleMask = uint.MaxValue,
            DepthStencilFormat = d.DepthFormat == Format.Unknown ? DXGI.Format.Unknown : Dx12Map.Format(d.DepthFormat),
            RenderTargetFormats = rtFormats,
            SampleDescription = new SampleDescription((uint)Math.Max(1, d.SampleCount), 0u),
        };
        Pso = dev.Device.CreateGraphicsPipelineState(desc);
    }
    private static DepthStencilOperationDescription Dx12StencilFace(StencilFaceState f)
    {
        return new()
        {
            StencilFunc = f.Compare switch { StencilCompare.Equal => ComparisonFunction.Equal, StencilCompare.NotEqual => ComparisonFunction.NotEqual, StencilCompare.Never => ComparisonFunction.Never, _ => ComparisonFunction.Always },
            StencilFailOp = Dx12StencilOp(f.Fail),
            StencilDepthFailOp = Dx12StencilOp(f.DepthFail),
            StencilPassOp = Dx12StencilOp(f.Pass),
        };
    }

    private static StencilOperation Dx12StencilOp(StencilOp o)
    {
        return o switch
        {
            StencilOp.Zero => StencilOperation.Zero,
            StencilOp.Replace => StencilOperation.Replace,
            StencilOp.IncrementWrap => StencilOperation.Increment,   // Vortice 'Increment' = wrapping incr
            StencilOp.DecrementWrap => StencilOperation.Decrement,   // Vortice 'Decrement' = wrapping decr
            StencilOp.Invert => StencilOperation.Invert,
            _ => StencilOperation.Keep,
        };
    }

    private static BlendDescription Dx12BlendState(BlendMode mode, bool colorWriteDisable)
    {
        BlendDescription b = BlendDescription.Opaque;
        ref RenderTargetBlendDescription rt = ref b.RenderTarget[0];
        switch (mode)
        {
            case BlendMode.AlphaBlend:
                rt.BlendEnable = true;
                rt.SourceBlend = Blend.SourceAlpha;
                rt.DestinationBlend = Blend.InverseSourceAlpha;
                rt.BlendOperation = BlendOperation.Add;
                rt.SourceBlendAlpha = Blend.One;
                rt.DestinationBlendAlpha = Blend.InverseSourceAlpha;
                rt.BlendOperationAlpha = BlendOperation.Add;
                break;
            case BlendMode.PremultipliedAlpha:
                rt.BlendEnable = true;
                rt.SourceBlend = Blend.One;
                rt.DestinationBlend = Blend.InverseSourceAlpha;
                rt.BlendOperation = BlendOperation.Add;
                rt.SourceBlendAlpha = Blend.One;
                rt.DestinationBlendAlpha = Blend.InverseSourceAlpha;
                rt.BlendOperationAlpha = BlendOperation.Add;
                break;
            case BlendMode.Additive:
                rt.BlendEnable = true;
                rt.SourceBlend = Blend.SourceAlpha;
                rt.DestinationBlend = Blend.One;
                rt.BlendOperation = BlendOperation.Add;
                rt.SourceBlendAlpha = Blend.One;
                rt.DestinationBlendAlpha = Blend.One;
                rt.BlendOperationAlpha = BlendOperation.Add;
                break;
            case BlendMode.Multiplicative:   // dst *= src; alpha {Zero, One} keeps the target alpha
                rt.BlendEnable = true;
                rt.SourceBlend = Blend.Zero;
                rt.DestinationBlend = Blend.SourceColor;
                rt.BlendOperation = BlendOperation.Add;
                rt.SourceBlendAlpha = Blend.Zero;
                rt.DestinationBlendAlpha = Blend.One;
                rt.BlendOperationAlpha = BlendOperation.Add;
                break;
        }
        // The NanoVG stencil-marking pass writes the stencil buffer only (no color).
        rt.RenderTargetWriteMask = colorWriteDisable ? ColorWriteEnable.None : ColorWriteEnable.All;
        return b;
    }
    protected override void Destroy()
    {
        Pso?.Dispose();
    }
}

internal sealed unsafe class Dx12CommandList : ICommandList, IGraphicsRecorder
{
    private readonly Dx12Device _dev;
    private D12PipelineLayout? _layout; private bool _heapsSet, _descDirty, _computeBind;
    private readonly GpuBufferView[] _cbv = new GpuBufferView[8];
    private readonly GpuBufferView[] _srvBuf = new GpuBufferView[16];
    private readonly GpuBufferView[] _uav = new GpuBufferView[16];
    private readonly D12TextureView?[] _texSrv = new D12TextureView[16];
    private readonly SamplerDesc[] _samp = new SamplerDesc[16];
    public ID3D12GraphicsCommandList List;
    public ID3D12CommandAllocator Allocator;
    public QueueKind Queue { get; }
    public Dx12CommandList(Dx12Device dev, ID3D12GraphicsCommandList list, ID3D12CommandAllocator alloc, QueueKind queue)
    {
        _dev = dev;
        List = list;
        Allocator = alloc;
        Queue = queue;
    }

    public void Begin()
    {
        Allocator.Reset();
        List.Reset(Allocator, null);
    }
    public void Close()
    {
        List.Close();
    }

    public void ResourceBarrier(ReadOnlySpan<ResourceBarrierDesc> barriers)
    {
        foreach (ResourceBarrierDesc b in barriers)
        {
            D12Resource? res = _dev.Lookup(b.ResourceHandle);
            ID3D12Resource? d3dres = (res as D12Texture)?.Resource ?? (res as D12Buffer)?.Resource;
            if (d3dres is null)
            {
                continue;
            }

            if (b.IsUavBarrier)
            {
                List.ResourceBarrierUnorderedAccessView(d3dres);
            }
            else if (b.Before != b.After)
            {
                List.ResourceBarrierTransition(d3dres, Dx12Map.State(b.Before), Dx12Map.State(b.After)); // skip same-state (D3D12 errors on before==after; the WAW flush is a Vulkan-only need)
            }
        }
    }

    public void WriteTimestamp(IQueryHeap heap, int index)
    {
        if (heap is D12QueryHeap qh)
        {
            List.EndQuery(qh.Heap, QueryType.Timestamp, (uint)index);
        }
    }
    public void CopyBufferToTexture(IBuffer src, ITexture dst, uint width, uint height)
    {
        CopyBufferToTextureRegion(src, dst, 0u, 0u, width, height, ((width * 4u) + 255u) & ~255u, 4u);
    }

    public void CopyBufferToTextureRegion(IBuffer src, ITexture dst, uint dstX, uint dstY, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel)
    {
        var b = (D12Buffer)src;
        var t = (D12Texture)dst;
        // D3D12 requires the source footprint row pitch to be 256-byte aligned; the caller stages aligned rows.
        System.Diagnostics.Debug.Assert((srcRowPitchBytes & 255u) == 0u, "srcRowPitchBytes must be 256-byte aligned for DX12 buffer->texture copies");
        var fp = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(Dx12Map.Format(t.Desc.Format), width, height, 1u, srcRowPitchBytes) };
        List.CopyTextureRegion(new TextureCopyLocation(t.Resource, 0), dstX, dstY, 0, new TextureCopyLocation(b.Resource, fp), null);
    }
    public void CopyTextureToBuffer(ITexture src, IBuffer dst, uint width, uint height)
    {
        var t = (D12Texture)src;
        var b = (D12Buffer)dst;
        uint rowPitch = ((width * 4u) + 255u) & ~255u;   // D3D12 requires 256-byte row alignment
        var fp = new PlacedSubresourceFootPrint { Offset = 0, Footprint = new SubresourceFootPrint(Dx12Map.Format(t.Desc.Format), width, height, 1u, rowPitch) };
        List.CopyTextureRegion(new TextureCopyLocation(b.Resource, fp), 0, 0, 0, new TextureCopyLocation(t.Resource, 0), null);
    }

    public void BeginRendering(ReadOnlySpan<ColorAttachment> colors, in DepthAttachment depth, uint width, uint height)
    {
        Span<CpuDescriptorHandle> rtvs = stackalloc CpuDescriptorHandle[colors.Length == 0 ? 1 : colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            var t = (D12Texture)_dev.Lookup(colors[i].TextureHandle)!;
            CpuDescriptorHandle h = _dev.AllocRtv();
            _dev.Device.CreateRenderTargetView(t.Resource, null, h);
            rtvs[i] = h;
        }

        CpuDescriptorHandle dsv = default;
        bool hasDepth = depth.HasDepth;
        if (hasDepth)
        {
            var dt = (D12Texture)_dev.Lookup(depth.TextureHandle)!;
            dsv = _dev.AllocDsv();
            // A read-only depth attachment
            // needs the READ_ONLY_DEPTH DSV flag, or the debug layer reports the DEPTH_READ | *_SHADER_RESOURCE state mismatch.
            DepthStencilViewFlags dsvFlags = depth.ReadOnly ? DepthStencilViewFlags.ReadOnlyDepth : DepthStencilViewFlags.None;
            if (dt.DepthSrv)
            {
                _dev.Device.CreateDepthStencilView(dt.Resource, new DepthStencilViewDescription { ViewDimension = DepthStencilViewDimension.Texture2D, Format = DXGI.Format.D32_Float, Flags = dsvFlags, Texture2D = new Texture2DDepthStencilView { MipSlice = 0 } }, dsv);
            }
            else if (depth.ReadOnly)
            {
                _dev.Device.CreateDepthStencilView(dt.Resource, new DepthStencilViewDescription { ViewDimension = DepthStencilViewDimension.Texture2D, Format = Dx12Map.Format(dt.Desc.Format), Flags = dsvFlags, Texture2D = new Texture2DDepthStencilView { MipSlice = 0 } }, dsv);
            }
            else
            {
                _dev.Device.CreateDepthStencilView(dt.Resource, null, dsv);
            }
        }

        List.OMSetRenderTargets((uint)colors.Length, colors.Length == 0 ? null : rtvs, false, hasDepth ? dsv : null);
        for (int i = 0; i < colors.Length; i++)
        {
            if (colors[i].Clear)
            {
                ClearColor c = colors[i].ClearValue;
                List.ClearRenderTargetView(rtvs[i], new Vortice.Mathematics.Color4(c.R, c.G, c.B, c.A));
            }
        }

        if (hasDepth && (depth.Clear || (depth.HasStencil && depth.ClearStencilOnLoad)))
        {
            // Clear the STENCIL aspect too when requested — the UI stencil-then-cover / path-clip needs a 0 stencil
            // each frame. (Previously only ClearFlags.Depth was passed, so stencil kept last-frame garbage on DX12.)
            ClearFlags flags = 0;
            if (depth.Clear)
            {
                flags |= ClearFlags.Depth;
            }

            if (depth.HasStencil && depth.ClearStencilOnLoad)
            {
                flags |= ClearFlags.Stencil;
            }

            List.ClearDepthStencilView(dsv, flags, depth.ClearDepth, depth.ClearStencil);
        }
    }
    public void EndRendering() { /* DX12 has no explicit end (no render pass object on the baseline path) */ }
    public void SetPipeline(IRenderPipeline pso)
    {
        if (pso is D12RenderPipeline p && p.Pso is not null)
        {
            _layout = p.LayoutObj;
            _computeBind = false;
            List.SetGraphicsRootSignature(p.LayoutObj.RootSignature);
            List.SetPipelineState(p.Pso);
            List.IASetPrimitiveTopology(p.IaTopology);
            _heapsSet = false;
            _descDirty = true;
        }
    }
    public void SetViewportScissor(float x, float y, float w, float h, float minD = 0f, float maxD = 1f)
    {
        List.RSSetViewport(x, y, w, h, minD, maxD);
        List.RSSetScissorRects(new Vortice.RawRect((int)x, (int)y, (int)(x + w), (int)(y + h)));
    }
    public void SetScissor(int x, int y, uint width, uint height) { List.RSSetScissorRects(new Vortice.RawRect(x, y, (int)(x + width), (int)(y + height))); }
    public void SetStencilReference(uint reference)
    {
        List.OMSetStencilRef(reference);
    }

    public void BindDescriptorTable(int rootSlot, DescriptorRange range) { /* baseline binds per-resource (below); explicit tables are an optional-tier path */ }
    public void BindConstantBuffer(int slot, in GpuBufferView cbv)
    {
        _cbv[slot] = cbv;
        _descDirty = true;
        if (_dev.Lifetime.Enabled)
        {
            _dev.Touch(cbv.Handle);
        }
    }
    public void BindTexture(int slot, ITextureView srv)
    {
        var v = (D12TextureView)srv;
        _texSrv[slot] = v;
        _descDirty = true;
        if (_dev.Lifetime.Enabled)
        {
            long s = _dev.Lifetime.RecordSerial;
            v.LastUseSerial = s;
            v.Texture.LastUseSerial = s;
        }
    }
    public void BindSampler(int slot, ISampler sampler)
    {
        _samp[slot] = ((D12Sampler)sampler).Desc;
        _descDirty = true;
    }
    public void BindStorageBuffer(int slot, in GpuBufferView buffer, bool uav)
    {
        if (uav)
        {
            _uav[slot] = buffer;
        }
        else
        {
            _srvBuf[slot] = buffer;
        }

        _descDirty = true;
        if (_dev.Lifetime.Enabled)
        {
            _dev.Touch(buffer.Handle);
        }
    }
    private void FlushDescriptors()
    {
        D12PipelineLayout? lay = _layout;
        if (lay is null)
        {
            return;
        }

        if (!_heapsSet && (lay.TableRootIndex >= 0 || lay.SamplerRootIndex >= 0))
        {
            _dev.SetBindingHeaps(List, lay.SamplerCount > 0);
            _heapsSet = true;
        }

        if (!_descDirty)
        {
            return;
        }

        if (lay.TableRootIndex >= 0)
        {
            int total = lay.CbvCount + lay.SrvCount + lay.TexSrvCount + lay.UavCount;
            (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) = _dev.AllocSrvTable(total);
            int inc = _dev.SvSrvInc;
            CpuDescriptorHandle h = cpu;
            for (int i = 0; i < lay.CbvCount; i++)
            {
                if (_dev.Lookup(_cbv[i].Handle) is D12Buffer b)
                {
                    _dev.Device.CreateConstantBufferView(new ConstantBufferViewDescription { BufferLocation = b.Resource.GPUVirtualAddress + _cbv[i].Offset, SizeInBytes = (uint)((_cbv[i].SizeBytes + 255) & ~255UL) }, h);
                }

                h.Ptr += (nuint)inc;
            }

            for (int i = 0; i < lay.SrvCount; i++)
            {
                if (_dev.Lookup(_srvBuf[i].Handle) is D12Buffer b)
                {
                    _dev.Device.CreateShaderResourceView(b.Resource, BufferSrvDesc(in _srvBuf[i]), h);
                }

                h.Ptr += (nuint)inc;
            }

            for (int i = 0; i < lay.TexSrvCount; i++)
            {
                D12TextureView? tv = _texSrv[i];
                if (tv is not null)
                {
                    if (tv.Texture.DepthSrv)
                    {
                        _dev.Device.CreateShaderResourceView(tv.Texture.Resource, new ShaderResourceViewDescription { ViewDimension = D3D.ShaderResourceViewDimension.Texture2D, Format = DXGI.Format.R32_Float, Shader4ComponentMapping = ShaderComponentMapping.Default, Texture2D = new Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 } }, h);
                    }
                    else
                    {
                        _dev.Device.CreateShaderResourceView(tv.Texture.Resource, null, h);
                    }
                }

                h.Ptr += (nuint)inc;
            }

            for (int i = 0; i < lay.UavCount; i++)
            {
                if (_dev.Lookup(_uav[i].Handle) is D12Buffer b)
                {
                    _dev.Device.CreateUnorderedAccessView(b.Resource, null, BufferUavDesc(in _uav[i]), h);
                }

                h.Ptr += (nuint)inc;
            }

            if (_computeBind)
            {
                List.SetComputeRootDescriptorTable((uint)lay.TableRootIndex, gpu);
            }
            else
            {
                List.SetGraphicsRootDescriptorTable((uint)lay.TableRootIndex, gpu);
            }
        }

        if (lay.SamplerRootIndex >= 0)
        {
            // PER-FRAME DEDUP: never allocate a fresh sampler table on every draw. See
            // Dx12Device.GetOrCreateSamplerTable for why that corrupts the world whenever a heavy UI screen
            // composites over it.
            GpuDescriptorHandle gpu = _dev.GetOrCreateSamplerTable(_samp.AsSpan(0, lay.SamplerCount));
            if (_computeBind)
            {
                List.SetComputeRootDescriptorTable((uint)lay.SamplerRootIndex, gpu);
            }
            else
            {
                List.SetGraphicsRootDescriptorTable((uint)lay.SamplerRootIndex, gpu);
            }
        }

        _descDirty = false;
    }
    // A view with a Stride is a STRUCTURED buffer (HLSL StructuredBuffer<T>); DX12 rejects a raw view bound where the
    // shader declares a structured one. Stride 0 = raw.
    private static ShaderResourceViewDescription BufferSrvDesc(in GpuBufferView v)
    {
        return v.Stride == 0
        ? new ShaderResourceViewDescription { ViewDimension = D3D.ShaderResourceViewDimension.Buffer, Format = DXGI.Format.R32_Typeless, Shader4ComponentMapping = ShaderComponentMapping.Default, Buffer = new BufferShaderResourceView { FirstElement = v.Offset / 4, NumElements = (uint)(v.SizeBytes / 4), Flags = BufferShaderResourceViewFlags.Raw } }
        : new ShaderResourceViewDescription { ViewDimension = D3D.ShaderResourceViewDimension.Buffer, Format = DXGI.Format.Unknown, Shader4ComponentMapping = ShaderComponentMapping.Default, Buffer = new BufferShaderResourceView { FirstElement = v.Offset / v.Stride, NumElements = (uint)(v.SizeBytes / v.Stride), StructureByteStride = v.Stride, Flags = BufferShaderResourceViewFlags.None } };
    }

    private static UnorderedAccessViewDescription BufferUavDesc(in GpuBufferView v)
    {
        return v.Stride == 0
            ? new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = DXGI.Format.R32_Typeless, Buffer = new BufferUnorderedAccessView { FirstElement = v.Offset / 4, NumElements = (uint)(v.SizeBytes / 4), Flags = BufferUnorderedAccessViewFlags.Raw } }
            : new UnorderedAccessViewDescription { ViewDimension = UnorderedAccessViewDimension.Buffer, Format = DXGI.Format.Unknown, Buffer = new BufferUnorderedAccessView { FirstElement = v.Offset / v.Stride, NumElements = (uint)(v.SizeBytes / v.Stride), StructureByteStride = v.Stride, Flags = BufferUnorderedAccessViewFlags.None } };
    }

    public void BindVertexBuffer(int slot, in GpuBufferView vb, uint stride)
    {
        if (_dev.Lookup(vb.Handle) is D12Buffer b)
        {
            List.IASetVertexBuffers((uint)slot, new VertexBufferView(b.Resource.GPUVirtualAddress + vb.Offset, (uint)vb.SizeBytes, stride));
        }
    }
    public void BindIndexBuffer(in GpuBufferView ib, bool index32)
    {
        if (_dev.Lookup(ib.Handle) is D12Buffer b)
        {
            List.IASetIndexBuffer(new IndexBufferView(b.Resource.GPUVirtualAddress + ib.Offset, (uint)ib.SizeBytes, index32 ? DXGI.Format.R32_UInt : DXGI.Format.R16_UInt));
        }
    }
    public void PushConstants<T>(in T data) where T : unmanaged
    {
        if (_layout is null || _layout.RootConstantsIndex < 0)
        {
            return;
        }

        int count = (Unsafe.SizeOf<T>() + 3) / 4;
        Span<uint> words = count <= 64 ? stackalloc uint[count] : new uint[count];
        words.Clear();
        MemoryMarshal.Write(MemoryMarshal.AsBytes(words), in data);
        fixed (uint* p = words)
        {
            if (_computeBind)
            {
                List.SetComputeRoot32BitConstants((uint)_layout.RootConstantsIndex, (uint)count, (nint)p, 0u);
            }
            else
            {
                List.SetGraphicsRoot32BitConstants((uint)_layout.RootConstantsIndex, (uint)count, (nint)p, 0u);
            }
        }
    }
    public void Draw(uint v, uint inst, uint fv, uint fi)
    {
        FlushDescriptors();
        List.DrawInstanced(v, inst, fv, fi);
    }
    public void DrawIndexed(uint idx, uint inst, uint fidx, int voff, uint fi)
    {
        FlushDescriptors();
        List.DrawIndexedInstanced(idx, inst, fidx, voff, fi);
    }
    public void DrawIndexedIndirect(in GpuBufferView args, uint offsetBytes, uint drawCount, uint stride)
    {
        FlushDescriptors();
        if (_dev.Lookup(args.Handle) is D12Buffer b)
        {
            List.ExecuteIndirect(_dev.DrawIndexedSignature(), drawCount, b.Resource, args.Offset + offsetBytes, null, 0UL);
        }
    }
    public void SetComputePipeline(D12ComputePipeline p)
    {
        _layout = p.LayoutObj;
        _computeBind = true;
        List.SetComputeRootSignature(p.LayoutObj.RootSignature);
        List.SetPipelineState(p.Pso);
        _heapsSet = false;
        _descDirty = true;
    }
    public void DispatchCompute(uint gx, uint gy, uint gz)
    {
        FlushDescriptors();
        List.Dispatch(gx, gy, gz);
    }
    public void DispatchComputeIndirect(in GpuBufferView args, uint offsetBytes)
    {
        FlushDescriptors();
        if (_dev.Lookup(args.Handle) is D12Buffer b)
        {
            List.ExecuteIndirect(_dev.DispatchSignature(), 1u, b.Resource, args.Offset + offsetBytes, null, 0UL);
        }
    }
    public void CopyBuffer(IBuffer src, ulong srcOffsetBytes, IBuffer dst, ulong dstOffsetBytes, ulong sizeBytes)
    {
        if (src is D12Buffer s && dst is D12Buffer d)
        {
            List.CopyBufferRegion(d.Resource, dstOffsetBytes, s.Resource, srcOffsetBytes, sizeBytes);
        }
    }
    public void CopyBufferToTextureMip(IBuffer src, ulong srcOffsetBytes, ITexture dst, uint mip, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel)
    {
        var b = (D12Buffer)src;
        var t = (D12Texture)dst;
        System.Diagnostics.Debug.Assert((srcRowPitchBytes & 255u) == 0u && (srcOffsetBytes & 511UL) == 0UL, "DX12 buffer->texture copies need 256-byte rows and a 512-byte aligned offset");
        var fp = new PlacedSubresourceFootPrint { Offset = srcOffsetBytes, Footprint = new SubresourceFootPrint(Dx12Map.Format(t.Desc.Format), width, height, 1u, srcRowPitchBytes) };
        List.CopyTextureRegion(new TextureCopyLocation(t.Resource, mip), 0, 0, 0, new TextureCopyLocation(b.Resource, fp), null);
    }
    // ---- GPU debug markers: D3D12 events in the legacy PIX blob format (PIX, RenderDoc). ----
    public void BeginEvent(string name, uint color)
    {
        if (Profiler.GpuMarkers)
        {
            Dx12Markers.Begin(List, color, name);
        }
    }
    public void EndEvent()
    {
        if (Profiler.GpuMarkers)
        {
            List.EndEvent();
        }
    }
    public void SetMarker(string name, uint color)
    {
        if (Profiler.GpuMarkers)
        {
            Dx12Markers.Marker(List, color, name);
        }
    }
    /// <summary>Record ResolveQueryData for a timestamp sub-range into a readback buffer (called before Close).</summary>
    public void ResolveTimestamps(IQueryHeap heap, int start, int count, IBuffer dst)
    {
        if (heap is D12QueryHeap qh && dst is D12Buffer b)
        {
            List.ResolveQueryData(qh.Heap, QueryType.Timestamp, (uint)start, (uint)count, b.Resource, 0UL);
        }
    }
    public void Dispose()
    {
        List.Dispose();
        Allocator.Dispose();
    }
}

internal sealed class Dx12Queue : ICommandQueue
{
    private readonly Dx12Device _dev;
    public ID3D12CommandQueue Queue;
    public QueueKind Kind { get; }
    public Dx12Queue(Dx12Device dev, ID3D12CommandQueue q, QueueKind kind)
    {
        _dev = dev;
        Queue = q;
        Kind = kind;
    }
    public ICommandList AcquireCommandList()
    {
        return _dev.AcquireCommandList(Kind);
    }

    public void Submit(ReadOnlySpan<ICommandList> lists, ReadOnlySpan<FenceWait> waitOn, ReadOnlySpan<FenceSignal> signal)
    {
        foreach (FenceWait w in waitOn)
        {
            _ = Queue.Wait(((D12Fence)w.Fence).Fence, w.Value);
        }

        if (lists.Length > 0)
        {
            var arr = new ID3D12CommandList[lists.Length];
            for (int i = 0; i < lists.Length; i++)
            {
                arr[i] = ((Dx12CommandList)lists[i]).List;
            }

            Queue.ExecuteCommandLists(arr);
        }

        foreach (FenceSignal s in signal)
        {
            _ = Queue.Signal(((D12Fence)s.Fence).Fence, s.Value);
        }

        if (_dev.Lifetime.Enabled)
        {
            long serial = _dev.Lifetime.OnSubmit();
            foreach (FenceSignal s in signal)
            {
                ((D12Fence)s.Fence).NoteSignal(s.Value, serial);
            }
        }
    }
    public ulong Signal(IFence fence)
    {
        var f = (D12Fence)fence;
        ulong v = f.CompletedValue + 1;
        _ = Queue.Signal(f.Fence, v);
        return v;
    }
    public void WaitIdle()
    {
        // No implicit device wait on DX12: signal a transient fence on this queue and CPU-block until it fires —
        // by then every previously-submitted command list on the queue has retired (shutdown drain).
        using var f = (D12Fence)_dev.CreateFence(0);
        ulong v = f.CompletedValue + 1;
        _ = Queue.Signal(f.Fence, v);
        f.Wait(v);
        _dev.Lifetime.OnIdle();   // every list submitted to this queue has retired
    }

    // ---- GPU debug markers on the queue timeline (the legacy PIX blob format). ----
    public void BeginEvent(string name, uint color)
    {
        if (Profiler.GpuMarkers)
        {
            Dx12Markers.Begin(Queue, color, name);
        }
    }
    public void EndEvent()
    {
        if (Profiler.GpuMarkers)
        {
            Queue.EndEvent();
        }
    }
    public void SetMarker(string name, uint color)
    {
        if (Profiler.GpuMarkers)
        {
            Dx12Markers.Marker(Queue, color, name);
        }
    }
}

/// <summary>The DX12 device. Runtime Windows-only; compile-verified cross-platform.</summary>
public sealed class Dx12Device : IGraphicsDevice
{
    internal ID3D12Device2 Device = null!;
    private ID3D12CommandQueue _computeQ = null!, _copyQ = null!;
    private ID3D12DescriptorHeap _rtvHeap = null!, _dsvHeap = null!;
    private int _rtvInc, _dsvInc, _rtvUsed, _dsvUsed;
    private const int RtvHeapSize = 256, DsvHeapSize = 64;
    private ID3D12DescriptorHeap _svSrvHeap = null!, _svSmpHeap = null!;
    private int _svSrvUsed, _svSmpUsed;
    // Shader-visible descriptor heaps are N-buffered by frames-in-flight (GBV use-after-free fix): the heap is
    // partitioned into FramesInFlight equal sub-ranges, one per in-flight frame. FlushDescriptors bakes each
    // draw's table IN PLACE in the heap, and the GpuScheduler runs FramesInFlight deep, so rewinding ONE shared
    // region to 0 every BeginFrame let frame N+1 overwrite the table slots frame N's still-in-flight command
    // list dereferenced -> GpuBasedValidationInvalidResource. Each frame now writes only its OWN sub-range; the
    // slot's frame fence was waited in BeginFrame before its sub-range is rewound, so it is provably idle.
    private int _svSrvPerFrame, _svSmpPerFrame;   // sub-range size per in-flight frame (0 until SetFramesInFlight)
    private int _svSrvBase, _svSmpBase;           // current frame's sub-range base (set by ResetFrameDescriptors)
    private int _svFramesInFlight = 1;            // partition count; 1 == legacy single region until set
    // PER-FRAME shader-visible descriptor budget; the heap is allocated SvSrvHeapSize * MaxFramesInFlight and
    // partitioned into one sub-range of SvSrvHeapSize per in-flight frame. SvSrvHeapSize MUST comfortably
    // exceed the worst-case per-frame CBV/SRV/UAV table demand, or AllocSrvTable would wrap and clobber earlier
    // draws' descriptors WITHIN a frame (the GPU reads tables at execute time). A batched UI frame issues one table
    // of 4 descriptors per draw (b0 paint CBV + b1 view CBV + t0 + t1 mask): a dense UI over a 4K window measured
    // ~9200 draws x 4 = 36,800 descriptors. The budget is 131072 per frame (3.6x that); 131072 x MaxFramesInFlight
    // (4) = 524,288, inside the Resource-Binding Tier 1 limit of 1,000,000 shader-visible CBV/SRV/UAV descriptors
    // (D3D12_MAX_SHADER_VISIBLE_DESCRIPTOR_HEAP_SIZE_TIER_1), about 16 MB of descriptor heap at 32 B each.
    // Shader-visible SAMPLER heaps are hardware-capped at 2048 total (D3D12_MAX_SHADER_VISIBLE_SAMPLER_HEAP_SIZE), so
    // SvSmpHeapSize stays 512 and the sampler-table cache below keeps the demand inside it.
    private const int SvSrvHeapSize = 131072, SvSmpHeapSize = 512;   // PER-FRAME budget (heap allocated * FramesInFlight)
    /// <summary>Test-only (InternalsVisibleTo MewStage.Rendering.Tests): per-in-flight-frame CBV/SRV/UAV descriptor
    /// sub-range capacity. Must exceed the worst-case per-frame descriptor demand (the batched UI ~8600).</summary>
    internal const int PerFrameSrvDescriptorCapacity = SvSrvHeapSize;
    /// <summary>Test-only: per-in-flight-frame SAMPLER sub-range capacity. Hardware-capped (shader-visible sampler
    /// heaps allow 2048 TOTAL), so this cannot be raised to cover a per-draw sampler table — the per-frame cache in
    /// <see cref="GetOrCreateSamplerTable"/> is what keeps the demand inside it.</summary>
    internal const int PerFrameSamplerDescriptorCapacity = SvSmpHeapSize;
    private const int MaxFramesInFlight = 4;   // heap is allocated for this many sub-ranges; actual FramesInFlight <= this
    internal int SvSrvInc { get; private set; }
    internal int SvSmpInc { get; private set; }
    private readonly Dictionary<ulong, D12Resource> _registry = [];
    private readonly DeferredDestroyQueue _deferred = new();   // the shared fence-keyed ring
    private long _retireFrame;
    /// <summary>The debug-only lifetime check (MEWSTAGE_GPU_VALIDATE=1 or MEWSTAGE_D3D12_DEBUG=1); the frame ring is sized
    /// for <see cref="MaxFramesInFlight"/> (the shader-visible heap partition bound).</summary>
    internal readonly GpuLifetimeValidator Lifetime = new(GpuLifetimeValidator.IsRequested("MEWSTAGE_D3D12_DEBUG"), MaxFramesInFlight);
    /// <summary>Lifetime violations detected (0 unless validation is on).</summary>
    public int LifetimeViolations => Lifetime.Violations;
    private long _handleCounter;
    private readonly Lock _gate = new();
    private readonly bool _useWarp;
    // DX12 debug-layer + GPU-Based Validation (GBV) + info-queue drain — OFF by default; enable with
    // MEWSTAGE_D3D12_DEBUG=1. GBV is slow, so it is strictly opt-in. D3D12-only.
    internal static readonly bool DebugEnabled =
        string.Equals(Environment.GetEnvironmentVariable("MEWSTAGE_D3D12_DEBUG"), "1", StringComparison.Ordinal);
    private ID3D12InfoQueue? _infoQueue;   // non-null only when DebugEnabled and the debug layer is present

    /// <inheritdoc />
    public GpuCapabilities Caps { get; private set; }
    /// <inheritdoc />
    public bool IsUma => Caps.IsUma;
    /// <inheritdoc />
    public ICommandQueue GraphicsQueue { get; private set; } = null!;
    /// <inheritdoc />
    public ICommandQueue ComputeQueue { get; private set; } = null!;
    /// <inheritdoc />
    public ICommandQueue CopyQueue { get; private set; } = null!;

    /// <summary>Creates the device on the first hardware adapter, or on the WARP software adapter.</summary>
    /// <param name="useWarp">Use the WARP software rasterizer (tests, machines without a GPU).</param>
    public Dx12Device(bool useWarp = false)
    {
        _useWarp = useWarp;
        Init();
    }

    private void Init()
    {
        EnableDebugLayerIfRequested();  // MEWSTAGE_D3D12_DEBUG=1 -> debug layer + GPU-Based Validation (before CreateDevice)
        EnableDredDiagnostics();   // must run BEFORE D3D12CreateDevice to take effect (Device Removed Extended Data)
        if (_useWarp)
        {
            using IDXGIFactory4 wf = DXGI.DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            using IDXGIAdapter warp = wf.EnumWarpAdapter<IDXGIAdapter>();
            D3D12.D3D12CreateDevice(warp, FeatureLevel.Level_12_0, out Device!).CheckError();
        }
        else
        {
            D3D12.D3D12CreateDevice(null, FeatureLevel.Level_12_0, out Device!).CheckError();
        }

        Console.WriteLine(_useWarp ? "DX12 device: WARP (software adapter, --warp)" : "DX12 device: hardware adapter");
        CaptureInfoQueueIfRequested();  // MEWSTAGE_D3D12_DEBUG=1 -> grab ID3D12InfoQueue for per-frame + device-removed drains
        GraphicsQueueRaw = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        _computeQ = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Compute));
        _copyQ = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Copy));
        _rtvHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, RtvHeapSize));
        _dsvHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.DepthStencilView, DsvHeapSize));
        _rtvInc = (int)Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.RenderTargetView);
        _dsvInc = (int)Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.DepthStencilView);
        _svSrvHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, SvSrvHeapSize * MaxFramesInFlight, DescriptorHeapFlags.ShaderVisible));
        _svSmpHeap = Device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.Sampler, SvSmpHeapSize * MaxFramesInFlight, DescriptorHeapFlags.ShaderVisible));
        SvSrvInc = (int)Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        SvSmpInc = (int)Device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);

        Caps = DetectCaps();
        GraphicsQueue = new Dx12Queue(this, GraphicsQueueRaw, QueueKind.Graphics);
        ComputeQueue = new Dx12Queue(this, _computeQ, QueueKind.AsyncCompute);
        CopyQueue = new Dx12Queue(this, _copyQ, QueueKind.Copy);
    }

    // ---- DX12 Debug Layer + GPU-Based Validation (GBV) + info-queue drain (Vortice 3.8.2). All env-gated
    //      (MEWSTAGE_D3D12_DEBUG=1); OFF by default because GBV is slow. D3D12-only.
    //      GBV reports a precise validation message AT the offending draw/dispatch — the decisive tool for a
    //      DXGI_ERROR_DEVICE_HUNG caused by a bad draw (garbage vertex/index/instance count, OOB resource). ----
    private static void EnableDebugLayerIfRequested()
    {
        if (!DebugEnabled)
        {
            return;
        }

        try
        {
            // ID3D12Debug.EnableDebugLayer() must precede device creation. ID3D12Debug1 adds GBV.
            if (D3D12.D3D12GetDebugInterface(out ID3D12Debug? dbg).Success && dbg is not null)
            {
                using (dbg)
                {
                    dbg.EnableDebugLayer();
                    Console.WriteLine("DX12 DEBUG: debug layer ENABLED (MEWSTAGE_D3D12_DEBUG=1).");
                    ID3D12Debug1? dbg1 = dbg.QueryInterfaceOrNull<ID3D12Debug1>();
                    if (dbg1 is not null)
                    {
                        using (dbg1)
                        {
                            dbg1.SetEnableGPUBasedValidation(true);
                            Console.WriteLine("DX12 DEBUG: GPU-Based Validation (GBV) ENABLED (slow; names the bad draw).");
                        }
                    }
                    else
                    {
                        Console.WriteLine("DX12 DEBUG: ID3D12Debug1 unavailable; GBV not enabled.");
                    }
                }
            }
            else
            {
                Console.WriteLine("DX12 DEBUG: debug interface unavailable (Graphics Tools not installed?).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DX12 DEBUG: enable failed ({ex.GetType().Name}: {ex.Message}).");
        }
    }

    private void CaptureInfoQueueIfRequested()
    {
        if (!DebugEnabled)
        {
            return;
        }

        try
        {
            _infoQueue = Device.QueryInterfaceOrNull<ID3D12InfoQueue>();
            if (_infoQueue is not null)
            {
                // Break-on is intrusive (halts under a debugger); we DRAIN instead so the message text is logged.
                // Keep all messages retrievable; do not mute.
                try
                {
                    _infoQueue.MuteDebugOutput = false;
                }
                catch { }

                Console.WriteLine("DX12 DEBUG: ID3D12InfoQueue captured; messages drained each Present + on device-removed.");
            }
            else
            {
                Console.WriteLine("DX12 DEBUG: ID3D12InfoQueue unavailable (debug layer not active).");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DX12 DEBUG: info-queue capture failed ({ex.Message}).");
        }
    }

    /// <summary>Drain + log every stored D3D12 debug/validation message (incl. the GBV message naming a bad
    /// draw). No-op unless MEWSTAGE_D3D12_DEBUG=1 and the info queue was captured. Called each Present and inside
    /// the device-removed dump. Cold/diagnostic — never on the steady-state fast path when disabled.</summary>
    internal void DrainInfoQueue(string where)
    {
        DrainInfoQueue(where, Console.Error);
    }

    // The writer is the DRED dump's when called from DumpDeviceRemoved, so the messages reach the crash log too.
    private void DrainInfoQueue(string where, System.IO.TextWriter w)
    {
        ID3D12InfoQueue? iq = _infoQueue;
        if (iq is null)
        {
            return;
        }

        try
        {
            ulong n = iq.NumStoredMessages;
            if (n == 0)
            {
                return;
            }

            w.WriteLine($"---- DX12 info-queue ({where}): {n} message(s) ----");
            for (ulong i = 0; i < n; i++)
            {
                try
                {
                    Message m = iq.GetMessage(i);
                    w.WriteLine($"    [{m.Severity}/{m.Category}] (id={m.Id}) {m.Description}");
                }
                catch (Exception ex)
                {
                    w.WriteLine($"    <message {i} unreadable: {ex.Message}>");
                }
            }

            iq.ClearStoredMessages();
        }
        catch (Exception ex)
        {
            try
            {
                w.WriteLine($"[DX12 info-queue] drain failed: {ex.Message}");
            }
            catch { }
        }
    }

    // ---- DX12 Device Removed Extended Data (DRED) — async-fault diagnostics (Vortice 3.8.2). DX12-only;
    //      the Vulkan backend is untouched. DXGI_ERROR_DEVICE_REMOVED is asynchronous: it surfaces at the NEXT
    //      device call AFTER the faulting GPU op, so the managed stack points at the wrong place. DRED records a
    //      GPU breadcrumb history (the LAST executed op == the fault) + the page-fault VA and the allocation
    //      (existing or recently-freed) that owned that VA. Enabled forced-on before device creation; dumped on
    //      any device-removed HRESULT. All best-effort: a missing runtime/feature must never break device init. ----
    private static void EnableDredDiagnostics()
    {
        try
        {
            // D3D12GetDebugInterface for the DRED settings COM object (no device needed; must precede CreateDevice).
            D3D12.D3D12GetDebugInterface(out ID3D12DeviceRemovedExtendedDataSettings? settings).CheckError();
            if (settings is null)
            {
                Console.WriteLine("DX12 DRED: settings interface unavailable.");
                return;
            }

            using (settings)
            {
                settings.SetAutoBreadcrumbsEnablement(DredEnablement.ForcedOn);
                settings.SetPageFaultEnablement(DredEnablement.ForcedOn);
            }

            Console.WriteLine("DX12 DRED: auto-breadcrumbs + page-fault tracking ForcedOn.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"DX12 DRED: unavailable on this runtime ({ex.GetType().Name}: {ex.Message}); device-removed dumps will be limited to DeviceRemovedReason.");
        }
    }

    /// <summary>True if a SharpGen HRESULT (or wrapped Result) is DXGI_ERROR_DEVICE_REMOVED (0x887A0005).</summary>
    private static bool IsDeviceRemoved(Exception ex)
    {
        return ex is SharpGen.Runtime.SharpGenException sge && sge.ResultCode == DXGI.ResultCode.DeviceRemoved;
    }

    /// <summary>Run <paramref name="op"/>; on a device-removed HRESULT dump DRED (breadcrumb + page fault) then
    /// rethrow. Catches the async fault at the next allocating call (CreateBuffer/CreateTexture) so the NEXT run
    /// prints the real faulting op BEFORE the process dies.</summary>
    private T GuardDeviceRemoved<T>(string where, Func<T> op)
    {
        try
        {
            return op();
        }
        catch (Exception ex) when (IsDeviceRemoved(ex))
        {
            DumpDeviceRemoved(where);
            throw;
        }
    }

    /// <summary>Dump DX12 Device Removed Extended Data to stderr: the device-removed HRESULT, the auto-breadcrumb
    /// GPU op history (the LAST executed op on each command list is the fault), and the page-fault GPU VA with the
    /// existing / recently-freed allocations (name + type) at that address. All wrapped — diagnostics never throw.
    /// Call this from any caught device-removed path (allocations, upload submit/wait, top-level frame catch). The
    /// text is also recorded in <see cref="GpuDeviceDiagnostics"/> so a process without a console keeps it.</summary>
    public void DumpDeviceRemoved(string where)
    {
        var w = new System.IO.StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            DumpDeviceRemovedTo(w, where);
        }
        finally
        {
            string text = w.ToString();
            try
            {
                Console.Error.Write(text);
            }
            catch { /* best-effort */ }

            GpuDeviceDiagnostics.Note(text);
        }
    }

    private void DumpDeviceRemovedTo(System.IO.TextWriter w, string where)
    {
        try
        {
            w.WriteLine("================ DX12 DEVICE REMOVED (DRED) ================");
            w.WriteLine($"  site: {where}");
            try
            {
                DrainInfoQueue("device-removed", w);
            }
            catch { /* best-effort */ }

            try
            {
                SharpGen.Runtime.Result reason = Device.DeviceRemovedReason;
                string name = reason == DXGI.ResultCode.DeviceRemoved ? "DXGI_ERROR_DEVICE_REMOVED"
                            : reason == DXGI.ResultCode.DeviceHung ? "DXGI_ERROR_DEVICE_HUNG"
                            : reason == DXGI.ResultCode.DeviceReset ? "DXGI_ERROR_DEVICE_RESET"
                            : reason == DXGI.ResultCode.DriverInternalError ? "DXGI_ERROR_DRIVER_INTERNAL_ERROR"
                            : reason == DXGI.ResultCode.InvalidCall ? "DXGI_ERROR_INVALID_CALL"
                            : reason.ToString();
                w.WriteLine($"  DeviceRemovedReason: 0x{reason.Code:X8} ({name})");
                // GetDeviceRemovedReason also reaches the crash log's GPU section (GpuDeviceDiagnostics)
                GpuDeviceDiagnostics.Fault($"GetDeviceRemovedReason 0x{reason.Code:X8} ({name}) at {where}");
            }
            catch (Exception ex)
            {
                w.WriteLine($"  DeviceRemovedReason: <unavailable: {ex.Message}>");
            }

            ID3D12DeviceRemovedExtendedData1? dred = null;
            try
            {
                dred = Device.QueryInterfaceOrNull<ID3D12DeviceRemovedExtendedData1>();
            }
            catch (Exception ex)
            {
                w.WriteLine($"  DRED1 unavailable: {ex.Message}");
            }

            if (dred is null)
            {
                w.WriteLine("  (DRED1 not present - was DRED enabled before device creation?)");
                w.WriteLine("===========================================================");
                return;
            }

            using (dred)
            {
                // --- Auto-breadcrumbs: per-command-list GPU op history; the first op the GPU did NOT complete
                //     (index == LastBreadcrumbValue) is the fault frontier == the hanging op. Vortice 3.8.2
                //     marshals CommandHistory (AutoBreadcrumbOp[]) + BreadcrumbCount + LastBreadcrumbValue (int?)
                //     + BreadcrumbContexts (DredBreadcrumbContext[]) on AutoBreadcrumbNode1. ---
                if (!DumpBreadcrumbsFrom(w, dred))
                {
                    DumpBreadcrumbsFromLegacy(w, dred);
                }

                // --- Page fault: VA + the allocation (existing or recently-freed) that owned it. ---
                try
                {
                    if (dred.GetPageFaultAllocationOutput1(out DredPageFaultOutput1? pf).Success)
                    {
                        w.WriteLine($"  --- Page fault @ GPU VA 0x{pf.PageFaultVA:X} ---");
                        DumpAllocNodes(w, "    existing allocation at VA", pf.HeadExistingAllocationNode);
                        DumpAllocNodes(w, "    RECENTLY-FREED allocation at VA (likely use-after-free)", pf.HeadRecentFreedAllocationNode);
                    }
                    else
                    {
                        w.WriteLine("  --- Page fault: no page-fault output (not a VA fault, or page-fault tracking disabled). ---");
                    }
                }
                catch (Exception ex)
                {
                    w.WriteLine($"  page-fault dump failed: {ex.Message}");
                }
            }

            w.WriteLine("===========================================================");
        }
        catch (Exception ex)
        {
            try
            {
                w.WriteLine($"[DRED] dump failed: {ex.Message}");
            }
            catch { }
        }
    }

    // Walk the AutoBreadcrumbNode1 linked list (GetAutoBreadcrumbsOutput1) and print each command list's GPU op
    // history, marking the fault frontier (** = first op that did NOT complete == the hanging op). Returns true
    // if at least one node was enumerated. (Vortice 3.8.2: CommandHistory + BreadcrumbCount + LastBreadcrumbValue.)
    private static bool DumpBreadcrumbsFrom(System.IO.TextWriter w, ID3D12DeviceRemovedExtendedData1 dred)
    {
        try
        {
            if (!dred.GetAutoBreadcrumbsOutput1(out DredAutoBreadcrumbsOutput1? brc).Success)
            {
                return false;
            }

            AutoBreadcrumbNode1? node = brc.HeadAutoBreadcrumbNode;
            if (node is null)
            {
                w.WriteLine("  --- Auto-breadcrumbs: none recorded (no nodes). ---");
                return false;
            }

            w.WriteLine("  --- Auto-breadcrumbs (GPU op history; ** = fault frontier == first op NOT completed) ---");
            int listIdx = 0;
            for (; node is not null && listIdx < 64; node = node.Next, listIdx++)
            {
                // BreadcrumbCount is authoritative for how many ops were RECORDED; CommandHistory marshals them.
                int count = node.BreadcrumbCount;
                AutoBreadcrumbOp[]? ops = node.CommandHistory;
                if (count <= 0 && ops is not null)
                {
                    count = ops.Length;
                }

                int? lastNullable = node.LastBreadcrumbValue;     // ops COMPLETED by the GPU (null => unknown)
                int completed = lastNullable ?? -1;
                w.WriteLine($"    [list \"{node.CommandListDebugName}\" queue \"{node.CommandQueueDebugName}\"] " +
                            $"{(completed < 0 ? "?" : completed.ToString())}/{count} ops completed:");
                if (ops is null || ops.Length == 0)
                {
                    w.WriteLine("        (no command history marshalled for this node)");
                }
                else
                {
                    // Print a tail window around the frontier so the log stays readable on long histories.
                    int frontier = (completed >= 0 && completed < ops.Length) ? completed : ops.Length - 1;
                    int from = Math.Max(0, frontier - 16);
                    int to = Math.Min(ops.Length, frontier + 4);
                    if (from > 0)
                    {
                        w.WriteLine($"        ... ({from} earlier ops elided)");
                    }

                    for (int i = from; i < to; i++)
                    {
                        // completed == number of ops finished, so ops[completed] is the FIRST not-completed op.
                        bool isFrontier = i == completed;
                        w.WriteLine($"        {(isFrontier ? "**" : "  ")} #{i}: {ops[i]}{(isFrontier ? "   <== HANGING / not completed" : "")}");
                    }

                    if (to < ops.Length)
                    {
                        w.WriteLine($"        ... ({ops.Length - to} later ops elided)");
                    }
                }
                // Richer per-op debug strings (DredBreadcrumbContext: BreadcrumbIndex + ContextString), if present.
                try
                {
                    DredBreadcrumbContext[]? ctxs = node.BreadcrumbContexts;
                    int cc = node.BreadcrumbContextsCount;
                    if (ctxs is not null && ctxs.Length > 0)
                    {
                        int show = Math.Min(ctxs.Length, cc > 0 ? cc : ctxs.Length);
                        w.WriteLine($"        breadcrumb contexts ({show}):");
                        for (int i = 0; i < show; i++)
                        {
                            w.WriteLine($"          @op#{ctxs[i].BreadcrumbIndex}: {ctxs[i].ContextString}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    w.WriteLine($"        (breadcrumb contexts unavailable: {ex.Message})");
                }
            }

            return listIdx > 0;
        }
        catch (Exception ex)
        {
            w.WriteLine($"  breadcrumb (Output1) dump failed: {ex.Message}");
            return false;
        }
    }

    // Fallback to the non-'1' GetAutoBreadcrumbsOutput (AutoBreadcrumbNode) when Output1 yields nothing.
    private static void DumpBreadcrumbsFromLegacy(System.IO.TextWriter w, ID3D12DeviceRemovedExtendedData1 dred)
    {
        try
        {
            if (!dred.GetAutoBreadcrumbsOutput(out DredAutoBreadcrumbsOutput? brc).Success)
            {
                w.WriteLine("  --- Auto-breadcrumbs: none recorded. ---");
                return;
            }

            AutoBreadcrumbNode? node = brc.HeadAutoBreadcrumbNode;
            if (node is null)
            {
                w.WriteLine("  --- Auto-breadcrumbs: none recorded (legacy, no nodes). ---");
                return;
            }

            w.WriteLine("  --- Auto-breadcrumbs [legacy Output] (GPU op history; ** = fault frontier) ---");
            int listIdx = 0;
            for (; node is not null && listIdx < 64; node = node.Next, listIdx++)
            {
                int count = node.BreadcrumbCount;
                AutoBreadcrumbOp[]? ops = node.CommandHistory;
                if (count <= 0 && ops is not null)
                {
                    count = ops.Length;
                }

                int completed = node.LastBreadcrumbValue ?? -1;
                w.WriteLine($"    [list \"{node.CommandListDebugName}\" queue \"{node.CommandQueueDebugName}\"] " +
                            $"{(completed < 0 ? "?" : completed.ToString())}/{count} ops completed:");
                if (ops is not null && ops.Length > 0)
                {
                    int frontier = (completed >= 0 && completed < ops.Length) ? completed : ops.Length - 1;
                    int from = Math.Max(0, frontier - 16);
                    int to = Math.Min(ops.Length, frontier + 4);
                    for (int i = from; i < to; i++)
                    {
                        w.WriteLine($"        {(i == completed ? "**" : "  ")} #{i}: {ops[i]}{(i == completed ? "   <== HANGING" : "")}");
                    }
                }
                else
                {
                    w.WriteLine("        (no command history marshalled)");
                }
            }
        }
        catch (Exception ex)
        {
            w.WriteLine($"  breadcrumb (legacy) dump failed: {ex.Message}");
        }
    }

    private static void DumpAllocNodes(System.IO.TextWriter w, string label, DredAllocationNode1? node)
    {
        if (node is null)
        {
            w.WriteLine($"{label}: (none)");
            return;
        }

        int n = 0;
        while (node is not null && n < 32)
        {
            w.WriteLine($"{label}: name=\"{node.ObjectName}\" type={node.AllocationType}");
            node = node.Next;
            n++;
        }
    }

    /// <summary>Detect optional-tier capabilities — parity with the Vulkan backend. Wrapped
    /// in try/catch so a missing/older feature query never fails device init; falls back to the portable baseline.</summary>
    private GpuCapabilities DetectCaps()
    {
        GpuCapabilities caps = GpuCapabilities.Baseline with { IndirectDraw = IndirectTier.MultiDraw, WaveLaneCount = 64 };
        try
        {
            bool uma = false;
            try
            {
                var a = new FeatureDataArchitecture1();
                if (Device.CheckFeatureSupport(D3D.Feature.Architecture1, ref a))
                {
                    uma = a.Uma;
                }
            }
            catch { }

            ResourceBindingTier binding = ResourceBindingTier.Tier1;
            try
            {
                binding = Device.Options.ResourceBindingTier;
            }
            catch { }
            // Bindless (descriptor indexing) needs Binding Tier 3 + SM 6.6 dynamic resources.
            bool sm66 = false;
            try
            {
                sm66 = Device.CheckHighestShaderModel(ShaderModel.Model6_6) >= ShaderModel.Model6_6;
            }
            catch { }

            BindlessTier bindless = (binding == ResourceBindingTier.Tier3 && sm66) ? BindlessTier.Tier1 : BindlessTier.None;
            int msaa = 1;
            try
            {
                foreach (int sc in new[] { 8, 4 })
                {
                    uint q = Device.CheckMultisampleQualityLevels(DXGI.Format.B8G8R8A8_UNorm, (uint)sc);
                    if (q > 0)
                    {
                        msaa = sc;
                        break;
                    }
                }
            }
            catch
            {
                msaa = 4;
            }

            caps = caps with { IsUma = uma, MaxMsaaSamples = msaa, Bindless = bindless };
        }
        catch
        {
            caps = GpuCapabilities.Baseline;
        }

        return caps;
    }
    internal ID3D12CommandQueue GraphicsQueueRaw { get; private set; } = null!;
    /// <summary>Graphics-queue timestamp frequency (ticks per second).</summary>
    internal ulong TimestampFrequency
    {
        get
        {
            try
            {
                _ = GraphicsQueueRaw.GetTimestampFrequency(out ulong f);
                return f;
            }
            catch
            {
                return 0UL;
            }
        }
    }
    /// <summary>A GPU timestamp sampled simultaneously with the CPU (ID3D12CommandQueue::GetClockCalibration), so a
    /// profiler can align the GPU lane to the CPU timeline. 0 if unsupported (WARP).</summary>
    internal ulong GpuClockCalibrationTick()
    {
        try
        {
            _ = GraphicsQueueRaw.GetClockCalibration(out ulong gpu, out _);
            return gpu;
        }
        catch
        {
            return 0UL;
        }
    }
    private ID3D12CommandSignature? _drawIndexedSig, _dispatchSig;
    /// <summary>The DISPATCH command signature (uint3 group counts, 12-byte stride) for IComputeRecorder.DispatchIndirect.</summary>
    internal ID3D12CommandSignature DispatchSignature()
    {
        if (_dispatchSig is null)
        {
            var desc = new CommandSignatureDescription(12, [new IndirectArgumentDescription { Type = IndirectArgumentType.Dispatch }]);
            _dispatchSig = Device.CreateCommandSignature<ID3D12CommandSignature>(desc, null);
        }

        return _dispatchSig;
    }
    internal ID3D12CommandSignature DrawIndexedSignature()
    {
        if (_drawIndexedSig is null)
        {
            var desc = new CommandSignatureDescription(20, [new IndirectArgumentDescription { Type = IndirectArgumentType.DrawIndexed }]);
            _drawIndexedSig = Device.CreateCommandSignature<ID3D12CommandSignature>(desc, null);
        }

        return _drawIndexedSig;
    }
    internal CpuDescriptorHandle AllocRtv()
    {
        CpuDescriptorHandle h = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        h.Ptr += (nuint)(_rtvUsed++ % RtvHeapSize * _rtvInc);
        return h;
    }
    internal CpuDescriptorHandle AllocDsv()
    {
        CpuDescriptorHandle h = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        h.Ptr += (nuint)(_dsvUsed++ % DsvHeapSize * _dsvInc);
        return h;
    }

    internal (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) AllocSrvTable(int n)
    {
        if (n <= 0)
        {
            n = 1;
        }
        // Allocate within THIS frame's sub-range [_svSrvBase, _svSrvBase + _svSrvPerFrame). It never wraps back to
        // the base within a frame: a silent modulo-wrap re-writes descriptor slots earlier draws in the SAME frame
        // still point at (the GPU reads tables at execute time) -> clobbered draws corrupt (the UI
        // washout bug). FAIL LOUD if the sub-range is exhausted instead. SvSrvHeapSize is sized to hold the worst-case
        // per-frame demand (~8600 for the batched UI) with headroom, so this cannot fire in practice; if it ever
        // does, the sub-range must be enlarged rather than allowed to corrupt.
        int perFrame = _svSrvPerFrame > 0 ? _svSrvPerFrame : SvSrvHeapSize;
        if (_svSrvUsed + n > _svSrvBase + perFrame)
        {
            throw new InvalidOperationException(
                $"[dx12] shader-visible CBV/SRV descriptor sub-range exhausted: {_svSrvUsed - _svSrvBase + n}/{perFrame} this frame " +
                "- UI/scene will corrupt (silent wrap would clobber earlier draws' descriptors). Increase SvSrvHeapSize.");
        }

        int start = _svSrvUsed;
        _svSrvUsed = start + n;
        CpuDescriptorHandle c = _svSrvHeap.GetCPUDescriptorHandleForHeapStart();
        c.Ptr += (nuint)(start * SvSrvInc);
        GpuDescriptorHandle g = _svSrvHeap.GetGPUDescriptorHandleForHeapStart();
        g.Ptr += (ulong)(start * SvSrvInc);
        return (c, g);
    }
    internal (CpuDescriptorHandle cpu, GpuDescriptorHandle gpu) AllocSamplerTable(int n)
    {
        if (n <= 0)
        {
            n = 1;
        }
        // THIS MUST FAIL LOUD, NEVER WRAP. A silent wrap does not reset to where the UI started: it resets to
        // _svSmpBase, which is where the WORLD's samplers live (the scene renders first). Once the UI's per-draw
        // tables push past the budget, the UI's s0 overwrites the scene's samplers, and the GPU reads descriptors at
        // execute time. Symptom: geometry perfect, every texture smeared, D3D12-only, and only with a heavy UI screen
        // over a live world. Two things keep it safe: GetOrCreateSamplerTable below collapses the UI's thousands of
        // identical allocations to ONE, and this throws like AllocSrvTable instead of corrupting silently. The heap
        // cannot simply be enlarged: shader-visible sampler heaps are hardware-capped at 2048 total.
        int perFrame = _svSmpPerFrame > 0 ? _svSmpPerFrame : SvSmpHeapSize;
        if (_svSmpUsed + n > _svSmpBase + perFrame)
        {
            throw new InvalidOperationException(
                $"[dx12] shader-visible SAMPLER sub-range exhausted: {_svSmpUsed - _svSmpBase + n}/{perFrame} this frame " +
                "- a silent wrap would overwrite the scene's samplers with the UI's (smeared textures + shadows). " +
                "Sampler heaps are hardware-capped, so the fix is fewer DISTINCT sampler sets per frame, not a bigger heap.");
        }

        int start = _svSmpUsed;
        _svSmpUsed = start + n;
        CpuDescriptorHandle c = _svSmpHeap.GetCPUDescriptorHandleForHeapStart();
        c.Ptr += (nuint)(start * SvSmpInc);
        GpuDescriptorHandle g = _svSmpHeap.GetGPUDescriptorHandleForHeapStart();
        g.Ptr += (ulong)(start * SvSmpInc);
        return (c, g);
    }

    // ---------------------------------------------------------------- sampler-table dedup
    //
    // A per-FRAME cache of sampler tables, keyed on the sampler set itself. Without it every draw allocates its own
    // table, so a batched UI frame burns one sampler descriptor per draw out of a 512 budget that is hardware-capped
    // and cannot be grown. In practice a frame uses a HANDFUL of distinct sampler sets (the UI's single linear-clamp
    // s0; the scene's texture sampler; a post pass's point sampler), so caching turns thousands of allocations into
    // about three.
    //
    // Keys are compared by VALUE, never by hash: SamplerDesc is IEquatable and the stored descs are compared
    // field-by-field on every probe. A hash-keyed cache would risk handing a draw the WRONG sampler on collision,
    // which is the very corruption this prevents.
    // Allocation-free in steady state: entries keep their SamplerDesc[] storage across frames and only the
    // live COUNT is reset, so a frame that uses the same sampler sets as the last one allocates nothing.
    private readonly List<(SamplerDesc[] Key, int Count, GpuDescriptorHandle Gpu)> _smpCache = [];
    private int _smpCacheCount;   // entries valid for the CURRENT frame; storage above is retained

    /// <summary>The GPU handle of a sampler table for <paramref name="descs"/>, creating it at most once per
    /// frame. Handles stay valid for the frame because the sub-range is only rewound by
    /// <see cref="ResetFrameDescriptors"/>, whose fence wait proves the frame is idle.</summary>
    internal GpuDescriptorHandle GetOrCreateSamplerTable(ReadOnlySpan<SamplerDesc> descs)
    {
        for (int e = 0; e < _smpCacheCount; e++)
        {
            (SamplerDesc[]? key, int count, GpuDescriptorHandle gpu) = _smpCache[e];
            if (count != descs.Length)
            {
                continue;
            }

            bool same = true;
            for (int i = 0; i < count; i++)
            {
                if (!key[i].Equals(descs[i]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return gpu;
            }
        }

        (CpuDescriptorHandle cpu, GpuDescriptorHandle g) = AllocSamplerTable(descs.Length);
        int inc = SvSmpInc;
        CpuDescriptorHandle h = cpu;
        for (int i = 0; i < descs.Length; i++)
        {
            SamplerDescription sd = Dx12Map.Sampler(descs[i]);
            Device.CreateSampler(ref sd, h);
            h.Ptr += (nuint)inc;
        }

        if (_smpCacheCount < _smpCache.Count)
        {
            SamplerDesc[] stored = _smpCache[_smpCacheCount].Key;
            SamplerDesc[] key = stored.Length >= descs.Length ? stored : new SamplerDesc[descs.Length];
            for (int i = 0; i < descs.Length; i++)
            {
                key[i] = descs[i];
            }

            _smpCache[_smpCacheCount] = (key, descs.Length, g);
        }
        else
        {
            var key = new SamplerDesc[Math.Max(descs.Length, 4)];
            for (int i = 0; i < descs.Length; i++)
            {
                key[i] = descs[i];
            }

            _smpCache.Add((key, descs.Length, g));
        }

        _smpCacheCount++;
        return g;
    }
    private readonly ID3D12DescriptorHeap[] _heaps1 = new ID3D12DescriptorHeap[1];
    private readonly ID3D12DescriptorHeap[] _heaps2 = new ID3D12DescriptorHeap[2];
    internal void SetBindingHeaps(ID3D12GraphicsCommandList list, bool withSampler)
    {
        if (withSampler)
        {
            _heaps2[0] = _svSrvHeap;
            _heaps2[1] = _svSmpHeap;
            list.SetDescriptorHeaps(2, _heaps2);
        }
        else
        {
            _heaps1[0] = _svSrvHeap;
            list.SetDescriptorHeaps(1, _heaps1);
        }
    }
    /// <summary>Rewind the shader-visible binding heaps at frame begin.</summary>
    /// <summary>Rewind the shader-visible binding heaps to the CURRENT in-flight frame's sub-range at frame begin
    ///. <paramref name="frameSlot"/> = frameNo % FramesInFlight; the slot's frame fence
    /// was waited in BeginFrame, so its sub-range is provably idle. N-buffering the heap stops frame N+1 from
    /// overwriting descriptor tables frame N's still-in-flight command list dereferences (GBV use-after-free fix).</summary>
    public void ResetFrameDescriptors(int frameSlot, int framesInFlight)
    {
        int fif = framesInFlight < 1 ? 1 : (framesInFlight > MaxFramesInFlight ? MaxFramesInFlight : framesInFlight);
        _svFramesInFlight = fif;
        _svSrvPerFrame = SvSrvHeapSize;   // each sub-range keeps the full per-frame budget (heap = budget * Max)
        _svSmpPerFrame = SvSmpHeapSize;
        int slot = ((frameSlot % fif) + fif) % fif;
        _svSrvBase = slot * _svSrvPerFrame;
        _svSrvUsed = _svSrvBase;
        _svSmpBase = slot * _svSmpPerFrame;
        _svSmpUsed = _svSmpBase;
        // The sampler-table cache hands out handles INTO this sub-range, so it MUST be invalidated in the same
        // breath as the rewind. Keeping the entries' storage (only the count resets) is what keeps the steady
        // state allocation-free.
        _smpCacheCount = 0;
    }

    internal ulong NextHandle()
    {
        return (ulong)Interlocked.Increment(ref _handleCounter);
    }

    internal void Register(D12Resource r)
    {
        lock (_gate)
        {
            _registry[r.Handle] = r;
        }
    }
    internal void Unregister(ulong h)
    {
        lock (_gate)
        {
            _ = _registry.Remove(h);
        }
    }
    internal D12Resource? Lookup(ulong h)
    {
        lock (_gate)
        {
            if (!_registry.TryGetValue(h, out D12Resource? r))
            {
                return null;
            }

            if (Lifetime.Enabled)
            {
                r.LastUseSerial = Lifetime.RecordSerial;   // every lookup is a command-list use
            }

            return r;
        }
    }
    internal void Touch(ulong h)
    {
        Lookup(h);
    }

    internal ICommandList AcquireCommandList(QueueKind kind)
    {
        CommandListType type = kind == QueueKind.AsyncCompute ? CommandListType.Compute : kind == QueueKind.Copy ? CommandListType.Copy : CommandListType.Direct;
        ID3D12CommandAllocator alloc = Device.CreateCommandAllocator(type);
        ID3D12GraphicsCommandList list = Device.CreateCommandList<ID3D12GraphicsCommandList>(0, type, alloc, null);
        list.Close();   // created open; caller calls Begin() which Reset()s
        return new Dx12CommandList(this, list, alloc, kind);
    }

    /// <summary>MEWSTAGE_GPU_POISON=1 at construction - every buffer and texture is created CREATE_NOT_ZEROED (default
    /// heap) and filled with <see cref="GpuPoison.Byte"/> before first use. Debug / test only.</summary>
    public bool PoisonNewAllocations => PoisonFill is not null;
    /// <summary>The fill byte of <see cref="PoisonNewAllocations"/> (GpuPoison.Byte, or GpuPoison.NanByte for MEWSTAGE_GPU_POISON=nan), or null.</summary>
    public byte? PoisonFill { get; } = GpuPoison.Requested();

    /// <summary>Fills a DEFAULT-heap buffer with the poison byte (a copy from a poisoned upload buffer; one-shot
    /// submit + wait, allocation time only). The buffer is in COMMON: promoted to COPY_DEST by the copy and decayed back
    /// to COMMON when the list completes (D3D12 implicit state transitions, buffers).</summary>
    internal void PoisonBuffer(ID3D12Resource buffer, ulong size)
    {
        PoisonOneShot(size, (list, upload) => list.CopyBufferRegion(buffer, 0, upload, 0, size));
    }

    /// <summary>Fills every subresource of a texture with the poison byte. Single-sample: a footprint copy from a
    /// poisoned upload buffer (the exact byte pattern in every format, depth included: whole-subresource copies).
    /// Multisampled (no buffer copy exists): a clear - color to the poison word read as a float (UNORM: Byte / 255, the
    /// exact byte), depth to Byte / 255 (a depth clear must lie in [0, 1]). The texture goes COMMON -> COPY_DEST /
    /// RENDER_TARGET / DEPTH_WRITE -> COMMON with explicit barriers (textures do not decay).</summary>
    internal unsafe void PoisonTexture(ID3D12Resource tex, in TextureDesc d, ResourceDescription rd, bool depthSrv)
    {
        if (d.Format is Format.BC5_UNorm or Format.BC7_UNorm or Format.BC7_UNorm_sRGB or Format.ASTC_4x4_UNorm or Format.ASTC_4x4_UNorm_sRGB)
        {
            return;   // always uploaded whole
        }

        bool depth = Dx12Map.IsDepth(d.Format);
        Format fmt = d.Format;
        byte fill = PoisonFill!.Value;
        float unorm = fill / (float)byte.MaxValue;
        if (Math.Max(1u, d.SampleCount) > 1u)
        {
            ResourceStates target = depth ? ResourceStates.DepthWrite : ResourceStates.RenderTarget;
            PoisonOneShot(0, (list, _) =>
            {
                list.ResourceBarrierTransition(tex, ResourceStates.Common, target);
                if (depth)
                {
                    CpuDescriptorHandle dsv = AllocDsv();
                    Device.CreateDepthStencilView(tex, depthSrv ? new D3D.DepthStencilViewDescription { ViewDimension = D3D.DepthStencilViewDimension.Texture2DMultisampled, Format = DXGI.Format.D32_Float } : null, dsv);
                    list.ClearDepthStencilView(dsv, ClearFlags.Depth | ClearFlags.Stencil, unorm, fill);
                }
                else
                {
                    CpuDescriptorHandle rtv = AllocRtv();
                    Device.CreateRenderTargetView(tex, null, rtv);
                    float f = fmt is Format.R16_Float or Format.R16G16_Float or Format.R16G16B16A16_Float or Format.R32_Float or Format.R32G32_Float
                        or Format.R32G32B32_Float or Format.R32G32B32A32_Float or Format.R11G11B10_Float ? GpuPoison.AsFloat(fill) : unorm;
                    list.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(f, f, f, f));
                }

                list.ResourceBarrierTransition(tex, target, ResourceStates.Common);
            });
            return;
        }
        // Subresources = mips x array layers x planes (depth-stencil formats carry a second, stencil plane).
        uint planes = d.Format is Format.D24_UNorm_S8_UInt or Format.D32_Float_S8_UInt ? 2u : 1u;
        uint count = Math.Max(1u, d.MipLevels) * Math.Max(1u, d.DepthOrArrayLayers) * planes;
        var layouts = new PlacedSubresourceFootPrint[count];
        ulong total;
        fixed (PlacedSubresourceFootPrint* pl = layouts)
        {
            Device.GetCopyableFootprints(rd, 0, count, 0, pl, null, null, out total);
        }

        PoisonOneShot(total, (list, upload) =>
        {
            list.ResourceBarrierTransition(tex, ResourceStates.Common, ResourceStates.CopyDest);
            for (uint i = 0; i < count; i++)
            {
                list.CopyTextureRegion(new TextureCopyLocation(tex, i), 0, 0, 0, new TextureCopyLocation(upload!, layouts[i]), null);
            }

            list.ResourceBarrierTransition(tex, ResourceStates.CopyDest, ResourceStates.Common);
        });
    }

    private readonly Lock _poisonGate = new();

    // One direct list + (when uploadBytes > 0) a poisoned upload buffer, executed on the graphics queue and waited.
    private unsafe void PoisonOneShot(ulong uploadBytes, Action<ID3D12GraphicsCommandList, ID3D12Resource?> record)
    {
        lock (_poisonGate)
        {
            using ID3D12CommandAllocator alloc = Device.CreateCommandAllocator(CommandListType.Direct);
            using ID3D12GraphicsCommandList list = Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, alloc, null);
            ID3D12Resource? upload = null;
            if (uploadBytes > 0)
            {
                upload = Device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None,
                    ResourceDescription.Buffer(uploadBytes), ResourceStates.GenericRead);
                void* p = null;
                _ = upload.Map(0u, &p);
                new Span<byte>(p, checked((int)uploadBytes)).Fill(PoisonFill!.Value);
                upload.Unmap(0u);
            }

            try
            {
                record(list, upload);
                list.Close();
                GraphicsQueueRaw.ExecuteCommandLists([list]);
                using ID3D12Fence fence = Device.CreateFence(0, FenceFlags.None);
                _ = GraphicsQueueRaw.Signal(fence, 1);
                nint evt = Dx12FenceEvent.ForCurrentThread();
                while (fence.CompletedValue < 1)
                {
                    _ = fence.SetEventOnCompletion(1, evt);
                    Dx12FenceEvent.Wait(evt, PoisonWaitSliceMs);
                }
            }
            finally
            {
                upload?.Dispose();
            }
        }

        GpuPoison.CountFill();
    }
    private const uint PoisonWaitSliceMs = 1000;   // re-check period of the poison wait (device removal makes CompletedValue max)

    /// <inheritdoc />
    public IBuffer CreateBuffer(in BufferDesc desc)
    {
        BufferDesc d = desc;
        return GuardDeviceRemoved($"CreateBuffer({desc.DebugName})", () => (IBuffer)new D12Buffer(this, d));
    }
    /// <inheritdoc />
    public ITexture CreateTexture(in TextureDesc desc)
    {
        TextureDesc d = desc;
        return GuardDeviceRemoved($"CreateTexture({desc.DebugName})", () => (ITexture)new D12Texture(this, d));
    }

    /// <inheritdoc />
    public void SetDebugName(IGpuResource resource, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        try
        {
            switch (resource)
            {
                case D12Buffer b:
                    b.Resource.Name = name;
                    break;
                case D12Texture t:
                    t.Resource.Name = name;
                    break;
                default:
                    break;
            }
        }
        catch { /* ID3D12Object naming is best-effort (DRED / capture only) */ }
    }
    /// <inheritdoc />
    public IBufferView CreateView(IBuffer buffer, in BufferViewDesc desc)
    {
        return new D12BufferView(this, buffer, desc);
    }

    /// <inheritdoc />
    public ITextureView CreateView(ITexture texture, in TextureViewDesc desc)
    {
        return new D12TextureView(this, (D12Texture)texture, desc);
    }

    /// <inheritdoc />
    public ISampler CreateSampler(in SamplerDesc desc)
    {
        return new D12Sampler(this, desc);
    }

    /// <inheritdoc />
    public IComputeRecorder CreateComputeRecorder(ICommandList list)
    {
        return new Compute.Dx12ComputeRecorder(this, (Dx12CommandList)list);
    }

    /// <inheritdoc />
    public IDescriptorHeap CreateDescriptorHeap(DescriptorKind kind, int capacity, bool shaderVisible)
    {
        return new D12DescriptorHeap(this, kind, capacity, shaderVisible);
    }

    /// <inheritdoc />
    public IPipelineLayout CreatePipelineLayout(in PipelineLayoutDesc desc)
    {
        return new D12PipelineLayout(this, desc);
    }

    /// <inheritdoc />
    public IFence CreateFence(ulong initialValue)
    {
        return new D12Fence(this, initialValue);
    }

    /// <inheritdoc />
    public IQueryHeap CreateTimestampQueryHeap(int capacity)
    {
        return new D12QueryHeap(this, capacity);
    }

    /// <inheritdoc />
    public ICommandSignature? CreateCommandSignature(in CommandSignatureDesc desc)
    {
        return null; // DX12 uses ExecuteIndirect via DrawIndexedSignature()
    }

    /// <inheritdoc />
    public IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
    {
        return new D12ComputePipeline(this, desc);
    }

    /// <inheritdoc />
    public IRenderPipeline CreateRenderPipeline(in RenderPipelineDesc desc)
    {
        return new D12RenderPipeline(this, desc);
    }

    /// <inheritdoc />
    public void DeferDestroy(IGpuResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        lock (_gate)
        {
            _deferred.Add(resource, _retireFrame);
        }
    }
    /// <inheritdoc />
    public void RetireDeferred(long currentFrame, int framesInFlight)
    {
        lock (_gate)
        {
            _retireFrame = currentFrame;
            // Free everything deferred at least framesInFlight frames ago: its slot fence has now been waited, so no
            // in-flight command list can still reference it. Newer entries (incl. those deferred this frame) survive.
            Lifetime.OnFrame(currentFrame, framesInFlight);
            _ = _deferred.Retire(currentFrame, framesInFlight);
        }
    }
    /// <inheritdoc />
    public int FlushDeferred()
    {
        lock (_gate)
        {
            Lifetime.OnIdle();
            return _deferred.Flush();
        }
    }   // teardown: caller drained the GPU
    /// <inheritdoc />
    public int PendingDeferredDestroys
    {
        get
        {
            lock (_gate)
            {
                return _deferred.Count;
            }
        }
    }
    /// <summary>The most deferred destroys ever pending at once (sizing evidence).</summary>
    public int DeferredHighWater
    {
        get
        {
            lock (_gate)
            {
                return _deferred.HighWater;
            }
        }
    }

    /// <inheritdoc />
    public DeviceStatus PollStatus()
    {
        SharpGen.Runtime.Result reason = Device.DeviceRemovedReason;
        return reason.Success ? DeviceStatus.Ok : DeviceStatus.Removed;
    }
    /// <inheritdoc />
    public event Action? OnDeviceLost;
    /// <inheritdoc />
    public bool TryRecreate()
    {
        OnDeviceLost?.Invoke();
        DestroyDevice();
        Init();
        return true;
    }

    private void DestroyDevice()
    {
        _drawIndexedSig?.Dispose();
        _drawIndexedSig = null;
        _dispatchSig?.Dispose();
        _dispatchSig = null;
        _rtvHeap?.Dispose();
        _dsvHeap?.Dispose();
        _svSrvHeap?.Dispose();
        _svSmpHeap?.Dispose();
        GraphicsQueueRaw?.Dispose();
        _computeQ?.Dispose();
        _copyQ?.Dispose();
        _infoQueue?.Dispose();
        _infoQueue = null;
        Device?.Dispose();
    }
    /// <inheritdoc />
    public void Dispose()
    {
        _ = FlushDeferred();
        DestroyDevice();
    }
}
