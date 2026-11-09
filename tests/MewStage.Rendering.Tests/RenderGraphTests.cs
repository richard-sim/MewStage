using System;
using System.Collections.Generic;
using System.Globalization;

using MewStage.Graphics;
using MewStage.Rendering.Graph;

using Xunit;

namespace MewStage.Rendering.Tests;

/// <summary>
/// Render-graph compiler tests: pass culling, transient aliasing (History never aliases; non-overlapping lifetimes
/// share a slot), barrier synthesis tracked per PHYSICAL slot, and ordered execution. All headless, no GPU.
/// </summary>
public sealed class RenderGraphTests
{
    private static RgTextureDesc Color(string n, uint w = 1920, uint h = 1080, Format f = Format.R11G11B10_Float, bool hist = false)
    {
        return new() { Name = n, Format = f, Width = w, Height = h, SampleCount = 1, MipLevels = 1, ArrayLayers = 1, History = hist };
    }

    private static RgTextureDesc Depth(string n, uint w = 1920, uint h = 1080)
    {
        return new() { Name = n, Format = Format.D24_UNorm_S8_UInt, Width = w, Height = h, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 };
    }

    private static RgTextureDesc Backbuffer => new() { Name = "backbuffer", Format = Format.R8G8B8A8_UNorm, Width = 1920, Height = 1080, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 };

    private static void NoOp(in RgPassContext ctx)
    {
    }

    [Fact]
    public void Culls_UnreachablePasses()
    {
        var g = new RenderGraph();
        RgHandle bb = g.ImportTexture(Backbuffer, backendHandle: 0xBB, ResourceState.Common, isGraphOutput: true);

        // A pass that writes a resource nobody consumes is culled; the pass writing the graph output survives.
        g.AddPass("dead", PassKind.Graphics, s =>
        {
            s.CreateColor(Color("dead"));
            s.SetExecute(NoOp);
        });
        g.AddPass("present", PassKind.Graphics, s =>
        {
            s.WriteColor(bb);
            s.SetExecute(NoOp);
        });

        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(g);
        var names = new List<string>();
        foreach (CompiledPass p in compiled.Passes)
        {
            names.Add(p.Name);
        }

        Assert.Contains("present", names);
        Assert.DoesNotContain("dead", names);
    }

    [Fact]
    public void Aliases_NonOverlappingTransients_ButNotHistory()
    {
        var g = new RenderGraph();
        RgHandle bb = g.ImportTexture(Backbuffer, 0xBB, ResourceState.Common, isGraphOutput: true);

        // a: written in p0, consumed in p1 (lifetime [0,1]); b: written in p2, consumed in p3 (lifetime [2,3]).
        // Same shape, non-overlapping: they share a slot. hist (History) is written in p0 and read in p3: its own slot.
        RgHandle a = default, b = default, hist = default;
        g.AddPass("p0", PassKind.Graphics, s =>
        {
            a = s.CreateColor(Color("a"));
            hist = s.CreateColor(Color("hist", hist: true));
            s.SetExecute(NoOp);
        });
        g.AddPass("p1", PassKind.Graphics, s =>
        {
            s.ReadTexture(a, ShaderStage.Pixel);
            s.SetExecute(NoOp);
        });
        g.AddPass("p2", PassKind.Graphics, s =>
        {
            b = s.CreateColor(Color("b"));
            s.SetExecute(NoOp);
        });
        g.AddPass("p3", PassKind.Graphics, s =>
        {
            s.ReadTexture(b, ShaderStage.Pixel);
            s.ReadTexture(hist, ShaderStage.Pixel);
            s.WriteColor(bb);
            s.SetExecute(NoOp);
        });

        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(g);
        int slotA = compiled.SlotOf(a.Index);
        int slotB = compiled.SlotOf(b.Index);
        int slotHist = compiled.SlotOf(hist.Index);

        Assert.True(slotA >= 0 && slotB >= 0 && slotHist >= 0);
        Assert.Equal(slotA, slotB);             // non-overlapping identical transients alias
        Assert.NotEqual(slotHist, slotA);       // History never aliases
        Assert.Equal(2, compiled.TransientSlotCount);
    }

    [Fact]
    public void AliasedTransients_BarrierFromTheSlotsRealState_NotFromCommon()
    {
        // Barrier state is tracked per PHYSICAL slot: when two same-shape, non-overlapping transients share a slot,
        // the second occupant's first barrier must start from the state the first occupant left the slot in, not
        // from Common. (A GPU resource has exactly one state; DX12 validates the Before state and fails otherwise.)
        // Every pass must reach the graph output or it is culled and its transient never gets a slot, which would
        // make the gate below pass vacuously: p1 therefore produces `m` for p3 to consume.
        var g = new RenderGraph();
        RgHandle bb = g.ImportTexture(Backbuffer, 0xBB, ResourceState.Common, isGraphOutput: true);
        RgHandle a = default, b = default, m = default;
        g.AddPass("p0", PassKind.Graphics, s =>
        {
            a = s.CreateDepth(Depth("a"));
            s.SetExecute(NoOp);
        });
        g.AddPass("p1", PassKind.Graphics, s =>
        {
            s.ReadTexture(a, ShaderStage.Pixel);
            m = s.CreateColor(Color("m"));
            s.SetExecute(NoOp);
        });
        g.AddPass("p2", PassKind.Graphics, s =>
        {
            b = s.CreateDepth(Depth("b"));
            s.SetExecute(NoOp);
        });
        g.AddPass("p3", PassKind.Graphics, s =>
        {
            s.ReadTexture(b, ShaderStage.Pixel);
            s.ReadTexture(m, ShaderStage.Pixel);
            s.WriteColor(bb);
            s.SetExecute(NoOp);
        });

        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(g);

        // Gate: the two transients really do share a slot, otherwise the assertion below proves nothing.
        int slot = compiled.SlotOf(a.Index);
        Assert.True(slot >= 0);
        Assert.Equal(slot, compiled.SlotOf(b.Index));

        // Replay every slot's state through the barriers in execution order and check each Before.
        var state = new Dictionary<int, ResourceState>();
        foreach (CompiledPass p in compiled.Passes)
        {
            foreach (ResourceBarrierDesc bar in p.BarriersView)
            {
                if (bar.IsUavBarrier)
                {
                    continue;
                }

                int sl = compiled.SlotOf((int)bar.ResourceHandle);
                if (sl < 0)
                {
                    continue;   // imported resources track their own state
                }

                ResourceState actual = state.TryGetValue(sl, out ResourceState st) ? st : ResourceState.Common;
                Assert.True(bar.Before == actual, string.Create(CultureInfo.InvariantCulture, $"slot {sl}: barrier says Before={bar.Before} but the slot is in {actual}"));
                state[sl] = bar.After;
            }
        }

        // And the specific shape of the bug: b's FIRST barrier must not claim Common.
        ResourceState? firstForB = null;
        foreach (CompiledPass p in compiled.Passes)
        {
            foreach (ResourceBarrierDesc bar in p.BarriersView)
            {
                if (!bar.IsUavBarrier && (int)bar.ResourceHandle == b.Index && firstForB is null)
                {
                    firstForB = bar.Before;
                }
            }
        }

        Assert.NotNull(firstForB);
        Assert.NotEqual(ResourceState.Common, firstForB.Value);
    }

    [Fact]
    public void AliasedTransients_ResetTheSlotOnce_NotOncePerOccupant()
    {
        // The terminal "reset every transient to Common" runs per SLOT: N aliased occupants get one reset, not N.
        var g = new RenderGraph();
        RgHandle bb = g.ImportTexture(Backbuffer, 0xBB, ResourceState.Common, isGraphOutput: true);
        RgHandle a = default, b = default, m = default;
        g.AddPass("p0", PassKind.Graphics, s =>
        {
            a = s.CreateDepth(Depth("a"));
            s.SetExecute(NoOp);
        });
        g.AddPass("p1", PassKind.Graphics, s =>
        {
            s.ReadTexture(a, ShaderStage.Pixel);
            m = s.CreateColor(Color("m"));
            s.SetExecute(NoOp);
        });
        g.AddPass("p2", PassKind.Graphics, s =>
        {
            b = s.CreateDepth(Depth("b"));
            s.SetExecute(NoOp);
        });
        g.AddPass("p3", PassKind.Graphics, s =>
        {
            s.ReadTexture(b, ShaderStage.Pixel);
            s.ReadTexture(m, ShaderStage.Pixel);
            s.WriteColor(bb);
            s.SetExecute(NoOp);
        });

        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(g);
        int slot = compiled.SlotOf(a.Index);
        Assert.Equal(slot, compiled.SlotOf(b.Index));

        CompiledPass last = compiled.Passes[compiled.Passes.Count - 1];
        int resets = 0;
        foreach (ResourceBarrierDesc bar in last.BarriersView)
        {
            if (!bar.IsUavBarrier && bar.After == ResourceState.Common && compiled.SlotOf((int)bar.ResourceHandle) == slot)
            {
                resets++;
            }
        }

        Assert.Equal(1, resets);
    }

    [Fact]
    public void Overlapping_Transients_DoNotAlias()
    {
        var g = new RenderGraph();
        RgHandle bb = g.ImportTexture(Backbuffer, 0xBB, ResourceState.Common, isGraphOutput: true);
        RgHandle a = default, b = default;
        g.AddPass("p0", PassKind.Graphics, s =>
        {
            a = s.CreateColor(Color("a"));
            b = s.CreateColor(Color("b"));
            s.SetExecute(NoOp);
        });
        g.AddPass("p1", PassKind.Graphics, s =>
        {
            s.ReadTexture(a, ShaderStage.Pixel);
            s.ReadTexture(b, ShaderStage.Pixel);
            s.WriteColor(bb);
            s.SetExecute(NoOp);
        });
        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(g);
        Assert.NotEqual(compiled.SlotOf(a.Index), compiled.SlotOf(b.Index));
    }

    [Fact]
    public void Synthesizes_BackbufferTransition_And_ExecutesInOrder()
    {
        var g = new RenderGraph();
        RgHandle bb = g.ImportTexture(Backbuffer, 0xBB, ResourceState.Common, isGraphOutput: true);
        var order = new List<string>();
        RgHandle scene = default;
        g.AddPass("opaque", PassKind.Graphics, s =>
        {
            scene = s.CreateColor(Color("scene"));
            s.SetExecute((in _) => order.Add("opaque"));
        });
        g.AddPass("tonemap", PassKind.Graphics, s =>
        {
            s.ReadTexture(scene, ShaderStage.Pixel);
            s.WriteColor(bb);
            s.SetExecute((in _) => order.Add("tonemap"));
        });

        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(g);

        // The imported backbuffer gets a Common -> RenderTarget transition before the pass that writes it.
        bool found = false;
        foreach (CompiledPass p in compiled.Passes)
        {
            foreach (ResourceBarrierDesc bar in p.BarriersView)
            {
                if (!bar.IsUavBarrier && (int)bar.ResourceHandle == bb.Index && bar.Before == ResourceState.Common && bar.After == ResourceState.RenderTarget)
                {
                    found = true;
                }
            }
        }

        Assert.True(found, "expected backbuffer Common->RenderTarget transition");

        // Execute resolves logical -> physical (the imported handle 0xBB) and runs the passes in order.
        compiled.BindTransientSlot(compiled.SlotOf(scene.Index), 0x5CE7E);
        var cmd = new RecordingCommandList();
        compiled.Execute(cmd, frameIndex: 7);
        Assert.Equal(2, order.Count);
        Assert.Equal("opaque", order[0]);
        Assert.Equal("tonemap", order[1]);
        Assert.Contains(cmd.Barriers, b => b.ResourceHandle == 0xBB && b.After == ResourceState.RenderTarget);
    }

    [Fact]
    public void ImportedResource_StartsFromItsDeclaredState()
    {
        // An import declares the state it is currently in; the first barrier must start from THAT, not Common.
        var g = new RenderGraph();
        RgTextureDesc desc = Color("viewport", 640, 360, Format.R8G8B8A8_UNorm);
        RgHandle vp = g.ImportTexture(desc, 0xA1, ResourceState.ShaderResource);
        RgHandle bb = g.ImportTexture(Backbuffer, 0xBB, ResourceState.Common, isGraphOutput: true);
        g.AddPass("scene", PassKind.Graphics, s =>
        {
            s.WriteColor(vp);
            s.SetExecute(NoOp);
        });
        g.AddPass("ui", PassKind.Graphics, s =>
        {
            s.ReadTexture(vp, ShaderStage.Pixel);
            s.WriteColor(bb);
            s.SetExecute(NoOp);
        });

        CompiledRenderGraph compiled = new RenderGraphCompiler().Compile(g);
        ResourceBarrierDesc? first = null;
        foreach (CompiledPass p in compiled.Passes)
        {
            foreach (ResourceBarrierDesc bar in p.BarriersView)
            {
                if (!bar.IsUavBarrier && (int)bar.ResourceHandle == vp.Index && first is null)
                {
                    first = bar;
                }
            }
        }

        Assert.NotNull(first);
        Assert.Equal(ResourceState.ShaderResource, first.Value.Before);
        Assert.Equal(ResourceState.RenderTarget, first.Value.After);
    }

    [Fact]
    public void Reuse_AcrossFrames_DoesNotLeakState()
    {
        var g = new RenderGraph();
        var compiler = new RenderGraphCompiler();
        for (int frame = 0; frame < 3; frame++)
        {
            g.Reset();
            RgHandle bb = g.ImportTexture(Backbuffer, 0xBB, ResourceState.Common, isGraphOutput: true);
            g.AddPass("present", PassKind.Graphics, s =>
            {
                s.WriteColor(bb);
                s.SetExecute(NoOp);
            });
            CompiledRenderGraph c = compiler.Compile(g);
            Assert.Single(c.Passes);
            Assert.Equal("present", c.Passes[0].Name);
        }
    }

    [Fact]
    public void StaleHandle_FromPreviousFrame_Throws()
    {
        var g = new RenderGraph();
        RgHandle bb = g.ImportTexture(Backbuffer, 0xBB, ResourceState.Common, isGraphOutput: true);
        g.Reset(); // invalidates bb
        Assert.Throws<InvalidOperationException>(() =>
            g.AddPass("p", PassKind.Graphics, s => s.WriteColor(bb)));
    }
}
