// Allocation gate: a steady-state frame through the render graph allocates 0 B, absolute, no baseline subtraction.
// Measured with GC.GetAllocatedBytesForCurrentThread over windows of frames after a warm-up, on the headless device
// with a recorder that records nothing, so every byte counted is CPU-side frame-building cost:
// Reset + AddPass (a cached setup delegate and the AddPass<TState> overload) + Compile + TransientPool + Execute.
using System;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Headless;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;

using Xunit;
using Xunit.Abstractions;

namespace MewStage.Rendering.Tests;

[Collection(Serial)]
public sealed class RenderGraphAllocationTests
{
    /// <summary>Allocation counters are per thread but the GC and the JIT are not: run alone.</summary>
    public const string Serial = "render-graph-allocations";

    private const int WarmFrames = 8, MeasuredFrames = 60, MeasureWindows = 3;
    private const uint W = 64, H = 48;

    private readonly ITestOutputHelper _out;

    public RenderGraphAllocationTests(ITestOutputHelper output)
    {
        _out = output;
    }

    internal static long BytesPerFrame(Action frame)
    {
        for (int i = 0; i < WarmFrames; i++)
        {
            frame();
        }

        long best = long.MaxValue;
        for (int w = 0; w < MeasureWindows; w++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < MeasuredFrames; i++)
            {
                frame();
            }

            long per = (GC.GetAllocatedBytesForCurrentThread() - before) / MeasuredFrames;
            if (per < best)
            {
                best = per;
            }
        }

        return best;
    }

    private static RgTextureDesc Tex(string n, Format f)
    {
        return new() { Name = n, Format = f, Width = W, Height = H, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 };
    }

    private sealed class Passes
    {
        private static readonly RgExecute Exec = static (in _) => { };

        public static readonly PassSetup<(RgHandle C, RgHandle D, RgHandle O)> Second =
            static (s, in t) =>
            {
                s.Read(t.C);
                s.Read(t.D);
                s.WriteColor(t.O);
                s.SetExecute(Exec);
            };

        public Passes()
        {
            First = s =>
            {
                Color = s.CreateColor(Tex("c", Format.R11G11B10_Float));
                Depth = s.CreateDepth(Tex("d", Format.D32_Float));
                s.SetExecute(Exec);
            };
        }

        public RgHandle Color { get; private set; }

        public RgHandle Depth { get; private set; }

        public Action<PassBuilder> First { get; }
    }

    [Fact]
    public void GraphFrame_ResetAddPassCompileExecute_Allocates0Bytes()
    {
        using var dev = new HeadlessDevice();
        var graph = new RenderGraph();
        var compiler = new RenderGraphCompiler();
        var cmd = new RecordingCommandList();
        using var transient = new TransientPool(dev);
        var p = new Passes();
        ulong frameIndex = 0;
        long bytes = BytesPerFrame(() =>
        {
            graph.Reset();
            transient.Reset();
            RgHandle bb = graph.ImportTexture(Tex("bb", Format.R8G8B8A8_UNorm), 0xBB, ResourceState.Common, isGraphOutput: true);
            graph.AddPass("first", PassKind.Graphics, p.First);
            graph.AddPass("second", PassKind.Graphics, (p.Color, p.Depth, bb), Passes.Second);
            CompiledRenderGraph compiled = compiler.Compile(graph);
            transient.Populate(compiled);
            compiled.Execute(cmd, frameIndex++);
            cmd.Barriers.Clear();
            cmd.Events.Clear();
        });
        _out.WriteLine("[alloc] graph frame " + bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " B/frame");
        Assert.Equal(0, bytes);
    }
}
