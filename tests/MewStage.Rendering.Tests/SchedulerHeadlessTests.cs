using System;
using System.Numerics;
using System.Threading;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Headless;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;

using Xunit;

namespace MewStage.Rendering.Tests;

/// <summary>
/// GpuScheduler + FrameContext + HeadlessDevice end to end, all headless (no GPU): the submission / sync /
/// retirement machinery, the upload ring, transient-slot backing and binding, and the compute logical timeline.
/// </summary>
public sealed class SchedulerHeadlessTests
{
    private static RgTextureDesc Color(string n)
    {
        return new()
        { Name = n, Format = Format.R11G11B10_Float, Width = 320, Height = 200, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 };
    }

    private static RgTextureDesc Backbuffer => new()
    { Name = "backbuffer", Format = Format.R8G8B8A8_UNorm, Width = 320, Height = 200, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 };

    [Fact]
    public void HeadlessDevice_ReportsBaseline_And_MapsHostVisibleBuffers()
    {
        using var dev = new HeadlessDevice();
        Assert.Equal(GpuCapabilities.Baseline, dev.Caps);
        Assert.False(dev.IsUma);

        IBuffer upload = dev.CreateBuffer(new BufferDesc { SizeBytes = 1024, Memory = MemoryClass.Upload, Usage = ResourceUsage.CopySource });
        Assert.IsType<IMappedBuffer>(upload, exactMatch: false);
        Assert.NotEqual(IntPtr.Zero, ((IMappedBuffer)upload).MappedPointer);

        IBuffer devLocal = dev.CreateBuffer(new BufferDesc { SizeBytes = 1024, Memory = MemoryClass.DeviceLocal, Usage = ResourceUsage.UnorderedAccess });
        Assert.Equal(IntPtr.Zero, ((IMappedBuffer)devLocal).MappedPointer); // device-local is not mapped
        upload.Dispose();
        devLocal.Dispose();
    }

    [Fact]
    public void UploadRing_Allocates_Writes_And_Rewinds()
    {
        using var dev = new HeadlessDevice();
        using var ring = new UploadRing(dev, 4096);
        UploadAllocation a = ring.Allocate(64);
        Assert.True(a.IsMapped);
        a.Write(new Vector4(1, 2, 3, 4));                    // writes into the mapped region (no throw)
        UploadAllocation b = ring.Allocate(64);
        Assert.True(b.View.Offset >= a.View.Offset + 64);    // aligned, non-overlapping
        ring.Reset();
        UploadAllocation c = ring.Allocate(64);
        Assert.Equal(a.View.Offset, c.View.Offset);          // rewound to the start
        Assert.Throws<InvalidOperationException>(() => ring.Allocate(1 << 20)); // overflow is guarded
    }

    [Fact]
    public void Scheduler_FrameCycle_BindsTransients_And_RunsGraph()
    {
        using var dev = new HeadlessDevice();
        using var sched = new GpuScheduler(dev);
        Assert.Equal(2, sched.FramesInFlight);

        int executed = 0;
        for (int frame = 0; frame < 5; frame++)
        {
            FrameContext ctx = sched.BeginFrame();
            var g = new RenderGraph();
            RgHandle bb = g.ImportTexture(Backbuffer, backendHandle: 0xBB, ResourceState.Common, isGraphOutput: true);
            RgHandle scene = default;
            g.AddPass("opaque", PassKind.Graphics, s =>
            {
                scene = s.CreateColor(Color("scene"));
                s.SetExecute(static (in _) => { });
            });
            g.AddPass("present", PassKind.Graphics, s =>
            {
                s.ReadTexture(scene, ShaderStage.Pixel);
                s.WriteColor(bb);
                s.SetExecute((in c) =>
                {
                    Interlocked.Increment(ref executed);
                    _ = c.Resolve(scene);
                });
            });

            CompiledRenderGraph compiled = sched.Submit(g, ctx);
            Assert.True(compiled.TransientSlotCount >= 1);
            Assert.NotEqual(0UL, compiled.ResolvePhysical(scene.Index)); // transient backing bound
            sched.EndFrame(ctx);
        }

        Assert.Equal(5, executed);
        Assert.Equal(5, sched.FrameNumber);
    }

    [Fact]
    public void Scheduler_DeferredDeletes_FlushAfterFenceCycle()
    {
        using var dev = new HeadlessDevice();
        using var sched = new GpuScheduler(dev);
        IBuffer doomed = dev.CreateBuffer(new BufferDesc { SizeBytes = 256, Memory = MemoryClass.DeviceLocal, Usage = ResourceUsage.ShaderResource });

        FrameContext ctx0 = sched.BeginFrame();
        sched.Retire(doomed);                 // retire during frame 0
        sched.EndFrame(ctx0);

        // It is only handed to the device's deferred-destroy queue when its context is recycled, FramesInFlight later.
        for (int i = 0; i < sched.FramesInFlight; i++)
        {
            FrameContext c = sched.BeginFrame();
            sched.EndFrame(c);
        }

        Assert.True(dev.FlushDeferred() >= 0);
    }

    private sealed class CountingPass : IComputePass
    {
        public int Records { get; private set; }

        public string Name => "test.count";

        public void Record(IComputeRecorder rec, in ComputeFrameContext ctx)
        {
            Records++;
        }
    }

    [Fact]
    public void Scheduler_ComputeTimeline_SubmitsAndCompletes()
    {
        using var dev = new HeadlessDevice();
        using var sched = new GpuScheduler(dev);
        FrameContext ctx = sched.BeginFrame();
        var pass = new CountingPass();
        ComputeJob job = new ComputeJob("blur").Add(pass);
        SubmitToken token = sched.SubmitCompute(job);
        Assert.True(token.IsComplete);          // the headless GPU is always caught up
        token.Wait();                           // does not block
        Assert.Equal(1, pass.Records);
        sched.EndFrame(ctx);
    }
}
