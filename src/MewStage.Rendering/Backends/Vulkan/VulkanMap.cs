// Vulkan enum/state mapping — MewStage.Rendering. Maps the backend-agnostic Core enums
// (Format, ResourceState, ResourceUsage, MemoryClass) to Vulkan, including the synchronization2 layout/access/
// stage triples the graph-synthesized barriers need. Pure static; no GPU calls.

using MewStage.Graphics;

using Vk = Silk.NET.Vulkan;

namespace MewStage.Rendering.Backends.Vulkan;

internal static class VulkanMap
{
    public static Vk.Format Format(Format f)
    {
        return f switch
        {
            Graphics.Format.R8_UNorm => Vk.Format.R8Unorm,
            Graphics.Format.R8_UInt => Vk.Format.R8Uint,
            Graphics.Format.R8G8_UNorm => Vk.Format.R8G8Unorm,
            Graphics.Format.R8G8B8A8_UNorm => Vk.Format.R8G8B8A8Unorm,
            Graphics.Format.R8G8B8A8_UNorm_sRGB => Vk.Format.R8G8B8A8Srgb,
            Graphics.Format.B8G8R8A8_UNorm => Vk.Format.B8G8R8A8Unorm,
            Graphics.Format.B8G8R8A8_UNorm_sRGB => Vk.Format.B8G8R8A8Srgb,
            Graphics.Format.R16_UInt => Vk.Format.R16Uint,
            Graphics.Format.R16_UNorm => Vk.Format.R16Unorm,
            Graphics.Format.R16_Float => Vk.Format.R16Sfloat,
            Graphics.Format.R16G16_Float => Vk.Format.R16G16Sfloat,
            Graphics.Format.R16G16B16A16_Float => Vk.Format.R16G16B16A16Sfloat,
            Graphics.Format.R32_UInt => Vk.Format.R32Uint,
            Graphics.Format.R32_Float => Vk.Format.R32Sfloat,
            Graphics.Format.R32G32_Float => Vk.Format.R32G32Sfloat,
            Graphics.Format.R32G32B32_Float => Vk.Format.R32G32B32Sfloat,
            Graphics.Format.R32G32B32A32_Float => Vk.Format.R32G32B32A32Sfloat,
            Graphics.Format.R11G11B10_Float => Vk.Format.B10G11R11UfloatPack32,
            Graphics.Format.R10G10B10A2_UNorm => Vk.Format.A2B10G10R10UnormPack32,
            Graphics.Format.D32_Float => Vk.Format.D32Sfloat,
            Graphics.Format.D24_UNorm_S8_UInt => Vk.Format.D24UnormS8Uint,
            Graphics.Format.D32_Float_S8_UInt => Vk.Format.D32SfloatS8Uint,
            Graphics.Format.BC5_UNorm => Vk.Format.BC5UnormBlock,
            Graphics.Format.BC7_UNorm => Vk.Format.BC7UnormBlock,
            Graphics.Format.BC7_UNorm_sRGB => Vk.Format.BC7SrgbBlock,
            Graphics.Format.ASTC_4x4_UNorm => Vk.Format.Astc4x4UnormBlock,
            Graphics.Format.ASTC_4x4_UNorm_sRGB => Vk.Format.Astc4x4SrgbBlock,
            _ => Vk.Format.Undefined,
        };
    }

    public static bool IsDepth(Format f)
    {
        return f is Graphics.Format.D32_Float or Graphics.Format.D24_UNorm_S8_UInt or Graphics.Format.D32_Float_S8_UInt;
    }

    public static bool HasStencil(Format f)
    {
        return f is Graphics.Format.D24_UNorm_S8_UInt or Graphics.Format.D32_Float_S8_UInt;
    }

    public static Vk.ImageAspectFlags Aspect(Format f)
    {
        if (!IsDepth(f))
        {
            return Vk.ImageAspectFlags.ColorBit;
        }

        Vk.ImageAspectFlags a = Vk.ImageAspectFlags.DepthBit;
        if (HasStencil(f))
        {
            a |= Vk.ImageAspectFlags.StencilBit;
        }

        return a;
    }

    public static Vk.ImageUsageFlags ImageUsage(ResourceUsage u)
    {
        Vk.ImageUsageFlags f = 0;
        if ((u & ResourceUsage.ShaderResource) != 0)
        {
            f |= Vk.ImageUsageFlags.SampledBit;
        }

        if ((u & ResourceUsage.UnorderedAccess) != 0)
        {
            f |= Vk.ImageUsageFlags.StorageBit;
        }

        if ((u & ResourceUsage.RenderTarget) != 0)
        {
            f |= Vk.ImageUsageFlags.ColorAttachmentBit;
        }

        if ((u & ResourceUsage.DepthStencil) != 0)
        {
            f |= Vk.ImageUsageFlags.DepthStencilAttachmentBit;
        }

        if ((u & ResourceUsage.CopySource) != 0)
        {
            f |= Vk.ImageUsageFlags.TransferSrcBit;
        }

        if ((u & ResourceUsage.CopyDest) != 0)
        {
            f |= Vk.ImageUsageFlags.TransferDstBit;
        }
        // RTs/UAVs are routinely read back / blitted; ensure transfer is permitted.
        f |= Vk.ImageUsageFlags.TransferSrcBit | Vk.ImageUsageFlags.TransferDstBit;
        return f;
    }

    public static Vk.BufferUsageFlags BufferUsage(ResourceUsage u)
    {
        Vk.BufferUsageFlags f = 0;
        if ((u & ResourceUsage.VertexBuffer) != 0)
        {
            f |= Vk.BufferUsageFlags.VertexBufferBit;
        }

        if ((u & ResourceUsage.IndexBuffer) != 0)
        {
            f |= Vk.BufferUsageFlags.IndexBufferBit;
        }

        if ((u & ResourceUsage.ConstantBuffer) != 0)
        {
            f |= Vk.BufferUsageFlags.UniformBufferBit;
        }

        if ((u & ResourceUsage.ShaderResource) != 0)
        {
            f |= Vk.BufferUsageFlags.StorageBufferBit;
        }

        if ((u & ResourceUsage.UnorderedAccess) != 0)
        {
            f |= Vk.BufferUsageFlags.StorageBufferBit;
        }

        if ((u & ResourceUsage.IndirectArgs) != 0)
        {
            f |= Vk.BufferUsageFlags.IndirectBufferBit;
        }

        if ((u & ResourceUsage.CopySource) != 0)
        {
            f |= Vk.BufferUsageFlags.TransferSrcBit;
        }

        if ((u & ResourceUsage.CopyDest) != 0)
        {
            f |= Vk.BufferUsageFlags.TransferDstBit;
        }

        f |= Vk.BufferUsageFlags.TransferSrcBit | Vk.BufferUsageFlags.TransferDstBit;
        return f;
    }

    public static Vk.MemoryPropertyFlags MemoryProps(MemoryClass m)
    {
        return m switch
        {
            MemoryClass.DeviceLocal => Vk.MemoryPropertyFlags.DeviceLocalBit,
            MemoryClass.Upload => Vk.MemoryPropertyFlags.HostVisibleBit | Vk.MemoryPropertyFlags.HostCoherentBit,
            MemoryClass.Readback => Vk.MemoryPropertyFlags.HostVisibleBit | Vk.MemoryPropertyFlags.HostCoherentBit | Vk.MemoryPropertyFlags.HostCachedBit,
            MemoryClass.Shared => Vk.MemoryPropertyFlags.DeviceLocalBit | Vk.MemoryPropertyFlags.HostVisibleBit | Vk.MemoryPropertyFlags.HostCoherentBit,
            _ => Vk.MemoryPropertyFlags.DeviceLocalBit,
        };
    }

    /// <summary>Map a ResourceState to the sync2 (layout, access, stage) triple for an IMAGE barrier.</summary>
    public static (Vk.ImageLayout layout, Vk.AccessFlags2 access, Vk.PipelineStageFlags2 stage) ImageState(ResourceState s)
    {
        return s switch
        {
            ResourceState.RenderTarget => (Vk.ImageLayout.ColorAttachmentOptimal, Vk.AccessFlags2.ColorAttachmentWriteBit | Vk.AccessFlags2.ColorAttachmentReadBit, Vk.PipelineStageFlags2.ColorAttachmentOutputBit),
            ResourceState.DepthWrite => (Vk.ImageLayout.DepthStencilAttachmentOptimal, Vk.AccessFlags2.DepthStencilAttachmentWriteBit | Vk.AccessFlags2.DepthStencilAttachmentReadBit, Vk.PipelineStageFlags2.EarlyFragmentTestsBit | Vk.PipelineStageFlags2.LateFragmentTestsBit),
            ResourceState.DepthRead => (Vk.ImageLayout.DepthStencilReadOnlyOptimal, Vk.AccessFlags2.DepthStencilAttachmentReadBit | Vk.AccessFlags2.ShaderSampledReadBit, Vk.PipelineStageFlags2.FragmentShaderBit | Vk.PipelineStageFlags2.EarlyFragmentTestsBit),
            // Depth-tested read-only AND sampled by a pixel OR compute shader in the same pass.
            // A combined flag value: the switch is exact-match, so it needs its own arm (ResourceStateMapTests).
            ResourceState.DepthReadShaderResource => (Vk.ImageLayout.DepthStencilReadOnlyOptimal, Vk.AccessFlags2.DepthStencilAttachmentReadBit | Vk.AccessFlags2.ShaderSampledReadBit, Vk.PipelineStageFlags2.FragmentShaderBit | Vk.PipelineStageFlags2.ComputeShaderBit | Vk.PipelineStageFlags2.EarlyFragmentTestsBit | Vk.PipelineStageFlags2.LateFragmentTestsBit),
            ResourceState.ShaderResource => (Vk.ImageLayout.ShaderReadOnlyOptimal, Vk.AccessFlags2.ShaderSampledReadBit, Vk.PipelineStageFlags2.FragmentShaderBit | Vk.PipelineStageFlags2.ComputeShaderBit),
            ResourceState.UnorderedAccess => (Vk.ImageLayout.General, Vk.AccessFlags2.ShaderStorageReadBit | Vk.AccessFlags2.ShaderStorageWriteBit, Vk.PipelineStageFlags2.ComputeShaderBit | Vk.PipelineStageFlags2.FragmentShaderBit),
            ResourceState.CopySource => (Vk.ImageLayout.TransferSrcOptimal, Vk.AccessFlags2.TransferReadBit, Vk.PipelineStageFlags2.AllTransferBit),
            ResourceState.CopyDest => (Vk.ImageLayout.TransferDstOptimal, Vk.AccessFlags2.TransferWriteBit, Vk.PipelineStageFlags2.AllTransferBit),
            ResourceState.Present => (Vk.ImageLayout.PresentSrcKhr, Vk.AccessFlags2.None, Vk.PipelineStageFlags2.BottomOfPipeBit),
            // vkCmdResolveImage reads/writes in the TRANSFER layouts (ResourceStateMapTests: every named state maps).
            ResourceState.ResolveSource => (Vk.ImageLayout.TransferSrcOptimal, Vk.AccessFlags2.TransferReadBit, Vk.PipelineStageFlags2.AllTransferBit),
            ResourceState.ResolveDest => (Vk.ImageLayout.TransferDstOptimal, Vk.AccessFlags2.TransferWriteBit, Vk.PipelineStageFlags2.AllTransferBit),
            _ => (Vk.ImageLayout.Undefined, Vk.AccessFlags2.None, Vk.PipelineStageFlags2.TopOfPipeBit),
        };
    }

    /// <summary>Map a ResourceState to (access, stage) for a BUFFER barrier (layout n/a).</summary>
    public static (Vk.AccessFlags2 access, Vk.PipelineStageFlags2 stage) BufferState(ResourceState s)
    {
        return s switch
        {
            ResourceState.VertexBuffer => (Vk.AccessFlags2.VertexAttributeReadBit, Vk.PipelineStageFlags2.VertexAttributeInputBit),
            ResourceState.IndexBuffer => (Vk.AccessFlags2.IndexReadBit, Vk.PipelineStageFlags2.IndexInputBit),
            ResourceState.ConstantBuffer => (Vk.AccessFlags2.UniformReadBit, Vk.PipelineStageFlags2.VertexShaderBit | Vk.PipelineStageFlags2.FragmentShaderBit | Vk.PipelineStageFlags2.ComputeShaderBit),
            // VertexShaderBit: a vertex shader may read storage buffers too.
            ResourceState.ShaderResource => (Vk.AccessFlags2.ShaderStorageReadBit, Vk.PipelineStageFlags2.VertexShaderBit | Vk.PipelineStageFlags2.FragmentShaderBit | Vk.PipelineStageFlags2.ComputeShaderBit),
            ResourceState.UnorderedAccess => (Vk.AccessFlags2.ShaderStorageReadBit | Vk.AccessFlags2.ShaderStorageWriteBit, Vk.PipelineStageFlags2.ComputeShaderBit),
            ResourceState.IndirectArgument => (Vk.AccessFlags2.IndirectCommandReadBit, Vk.PipelineStageFlags2.DrawIndirectBit),
            ResourceState.CopySource => (Vk.AccessFlags2.TransferReadBit, Vk.PipelineStageFlags2.AllTransferBit),
            ResourceState.CopyDest => (Vk.AccessFlags2.TransferWriteBit, Vk.PipelineStageFlags2.AllTransferBit),
            _ => (Vk.AccessFlags2.MemoryReadBit | Vk.AccessFlags2.MemoryWriteBit, Vk.PipelineStageFlags2.AllCommandsBit),
        };
    }
}
