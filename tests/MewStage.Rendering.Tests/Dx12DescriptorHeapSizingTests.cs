// Device-free sizing assertions for the DX12 shader-visible descriptor heaps. They encode two lessons:
//
//  1. The per-in-flight-frame CBV/SRV/UAV sub-range must hold the worst-case UI frame, because the allocator FAILS
//     LOUD instead of wrapping (a wrap within a frame overwrites descriptors the GPU reads at execute time: earlier
//     draws then sample a later draw's resources, which looks like a washed-out or smeared UI).
//  2. The SAMPLER heap is hardware-capped (2048 shader-visible samplers in total), so a per-draw sampler table
//     cannot fit a heavy UI frame and no resize can make it fit: the device dedups sampler tables per frame by VALUE,
//     and the sampler allocator fails loud too.
//
// These cannot run the GPU; the dedup's real behavior is the DX12 hand-test on Windows.
using MewStage.Rendering.Backends.Dx12;

using Xunit;

namespace MewStage.Rendering.Tests;

public sealed class Dx12DescriptorHeapSizingTests
{
    /// <summary>Descriptors one UI draw binds: b0 + b1 (CBVs), t0 + t1 (texture SRVs). The sampler is in its own heap.</summary>
    private const int DescriptorsPerUiDraw = 4;

    /// <summary>A heavy UI frame: a full-screen docked workbench with every panel populated at 4K.</summary>
    private const int WorstCaseUiDraws = 10_000;

    [Fact]
    public void PerFrameSrvSubRange_HoldsAHeavyUiFrame_TwiceOver()
    {
        // Twice over: the scene passes share the sub-range in the same frame.
        Assert.True(
            Dx12Device.PerFrameSrvDescriptorCapacity >= 2 * WorstCaseUiDraws * DescriptorsPerUiDraw,
            $"per-frame CBV/SRV/UAV sub-range {Dx12Device.PerFrameSrvDescriptorCapacity} must hold a heavy UI frame " +
            $"({WorstCaseUiDraws} draws x {DescriptorsPerUiDraw}) twice over");
    }

    [Fact]
    public void PerFrameSrvSubRange_StaysInsideTheTier1HeapLimit()
    {
        // Four in-flight sub-ranges share one heap; D3D12 Resource Binding Tier 1 allows 1,000,000 shader-visible
        // CBV/SRV/UAV descriptors per heap.
        Assert.True(4L * Dx12Device.PerFrameSrvDescriptorCapacity <= 1_000_000);
    }

    [Fact]
    public void PerFrameSamplerSubRange_CannotHoldAPerDrawSamplerTable()
    {
        // The premise of the dedup, asserted rather than assumed: one sampler per draw does NOT fit, and no heap resize
        // can make it fit (D3D12_MAX_SHADER_VISIBLE_SAMPLER_HEAP_SIZE is 2048 in total, across all frames).
        Assert.True(
            Dx12Device.PerFrameSamplerDescriptorCapacity < WorstCaseUiDraws,
            $"per-frame sampler sub-range {Dx12Device.PerFrameSamplerDescriptorCapacity} vs {WorstCaseUiDraws} UI draws: " +
            "a per-draw sampler table cannot fit, which is why GetOrCreateSamplerTable dedups per frame.");
        Assert.True(4L * Dx12Device.PerFrameSamplerDescriptorCapacity <= 2048);
    }

    [Fact]
    public void PerFrameSamplerSubRange_ComfortablyHoldsTheDedupedDemand()
    {
        // With dedup the demand is (distinct sampler sets per frame) x (samplers per set), a handful by construction:
        // the UI's single linear-clamp sampler, a scene pass's texture sampler, a post pass's point sampler.
        const int DistinctSamplerSetsPerFrame = 8;   // generous: the demo uses about 2
        const int WorstCaseSamplersPerSet = 2;
        const int DedupedDemand = DistinctSamplerSetsPerFrame * WorstCaseSamplersPerSet;
        Assert.True(
            Dx12Device.PerFrameSamplerDescriptorCapacity >= DedupedDemand * 4,
            $"per-frame sampler sub-range {Dx12Device.PerFrameSamplerDescriptorCapacity} should hold the deduped demand {DedupedDemand} with room to spare.");
    }
}
