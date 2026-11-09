// Mapping completeness for ResourceState on both backends. VulkanMap's switches are exact-match, so a combined flag
// value (DepthReadShaderResource) silently falls to the Undefined arm unless it has its own; these tests enumerate
// the enum so the next state added cannot repeat that.
using System;

using MewStage.Graphics;
using MewStage.Rendering.Backends.Dx12;
using MewStage.Rendering.Backends.Vulkan;

using Silk.NET.Vulkan;

using Xunit;

using Format = MewStage.Graphics.Format;

namespace MewStage.Rendering.Tests;

public sealed class ResourceStateMapTests
{
    private static readonly ResourceState[] ImageStates =
    [
        ResourceState.ShaderResource, ResourceState.UnorderedAccess, ResourceState.RenderTarget, ResourceState.DepthWrite,
        ResourceState.DepthRead, ResourceState.CopySource, ResourceState.CopyDest, ResourceState.Present,
        ResourceState.ResolveSource, ResourceState.ResolveDest, ResourceState.DepthReadShaderResource,
    ];

    private static readonly ResourceState[] BufferStates =
    [
        ResourceState.VertexBuffer, ResourceState.IndexBuffer, ResourceState.ConstantBuffer, ResourceState.IndirectArgument,
        ResourceState.ShaderResource, ResourceState.UnorderedAccess, ResourceState.CopySource, ResourceState.CopyDest,
    ];

    [Fact]
    public void EveryNamedState_IsClassified()
    {
        // Gate: a state added to the enum must be listed above as image-applicable, buffer-applicable or both.
        foreach (ResourceState s in Enum.GetValues<ResourceState>())
        {
            if (s == ResourceState.Common)
            {
                continue;
            }

            Assert.True(Array.IndexOf(ImageStates, s) >= 0 || Array.IndexOf(BufferStates, s) >= 0, "unclassified ResourceState " + s);
        }
    }

    [Fact]
    public void Vulkan_EveryImageState_HasALayout()
    {
        foreach (ResourceState s in ImageStates)
        {
            (ImageLayout layout, _, PipelineStageFlags2 stage) = VulkanMap.ImageState(s);
            Assert.True(layout != ImageLayout.Undefined, "VulkanMap.ImageState has no arm for " + s);
            Assert.True(stage != PipelineStageFlags2.TopOfPipeBit, "VulkanMap.ImageState stage is the default for " + s);
        }

        (ImageLayout depthLayout, AccessFlags2 depthAccess, PipelineStageFlags2 depthStage) = VulkanMap.ImageState(ResourceState.DepthReadShaderResource);
        Assert.Equal(ImageLayout.DepthStencilReadOnlyOptimal, depthLayout);
        Assert.NotEqual(AccessFlags2.None, depthAccess & AccessFlags2.ShaderSampledReadBit);
        Assert.NotEqual(PipelineStageFlags2.None, depthStage & PipelineStageFlags2.FragmentShaderBit);
    }

    [Fact]
    public void Vulkan_EveryBufferState_HasAccessMask()
    {
        (AccessFlags2 access, PipelineStageFlags2 stage) common = VulkanMap.BufferState(ResourceState.Common);
        foreach (ResourceState s in BufferStates)
        {
            Assert.True(VulkanMap.BufferState(s) != common, "VulkanMap.BufferState falls to the catch-all for " + s);
        }

        Assert.NotEqual(PipelineStageFlags2.None, VulkanMap.BufferState(ResourceState.IndirectArgument).stage & PipelineStageFlags2.DrawIndirectBit);
        Assert.NotEqual(PipelineStageFlags2.None, VulkanMap.BufferState(ResourceState.ShaderResource).stage & PipelineStageFlags2.VertexShaderBit);   // a vertex shader may read a storage buffer
    }

    [Fact]
    public void Dx12_EveryNamedState_MapsToABit()
    {
        foreach (ResourceState s in Enum.GetValues<ResourceState>())
        {
            if (s is ResourceState.Common or ResourceState.Present)
            {
                continue;   // D3D12_RESOURCE_STATE_PRESENT == COMMON == 0
            }

            Assert.True(Dx12Map.State(s) != 0, "Dx12Map.State is Common for " + s);
        }

        Vortice.Direct3D12.ResourceStates d = Dx12Map.State(ResourceState.DepthReadShaderResource);
        Assert.NotEqual(Vortice.Direct3D12.ResourceStates.Common, d & Vortice.Direct3D12.ResourceStates.DepthRead);
        Assert.NotEqual(Vortice.Direct3D12.ResourceStates.Common, d & Vortice.Direct3D12.ResourceStates.PixelShaderResource);
        Assert.NotEqual(Vortice.Direct3D12.ResourceStates.Common, d & Vortice.Direct3D12.ResourceStates.NonPixelShaderResource);
    }

    [Fact]
    public void Formats_RoundTripOnBothBackends()
    {
        // Every format the engine names must map on both backends (Unknown excepted).
        foreach (Format f in Enum.GetValues<Format>())
        {
            if (f == Format.Unknown)
            {
                continue;
            }

            Assert.True(VulkanMap.Format(f) != Silk.NET.Vulkan.Format.Undefined, "VulkanMap.Format has no arm for " + f);
            if (f is Format.ASTC_4x4_UNorm or Format.ASTC_4x4_UNorm_sRGB)
            {
                continue;   // Direct3D 12 has no ASTC formats; the DX12 backend reports them unsupported
            }

            Assert.True(Dx12Map.Format(f) != Vortice.DXGI.Format.Unknown, "Dx12Map.Format has no arm for " + f);
        }
    }
}
