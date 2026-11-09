// Vulkan 1.3 IGraphicsDevice backend — MewStage.Rendering. The full device
// contract over Silk.NET.Vulkan: queues, buffers/textures/views, descriptor heaps, pipeline layouts, graphics
// + compute PSOs from cooked SPIR-V, synchronization2 barriers, timeline-semaphore fences, timestamp queries,
// deferred destruction, and device-loss handling. Built on the verified offscreen nucleus (VulkanOffscreen).
// Vulkan-1.3 baseline: dynamic rendering + synchronization2 + timeline semaphores. Runs on
// Mesa lavapipe for headless render tests. NativeAOT-safe (Silk.NET source-gen P/Invoke).

using System;
using System.Collections.Generic;
using System.Threading;

using MewStage.Graphics;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan.Extensions.EXT;

using Vk = Silk.NET.Vulkan;

namespace MewStage.Rendering.Backends.Vulkan;

internal abstract class VkResource : IGpuResource
{
    public ulong Handle { get; }
    protected readonly VulkanDevice Dev;

    protected VkResource(VulkanDevice dev)
    {
        Dev = dev;
        Handle = dev.NextHandle();
        dev.Register(this);
    }
    public bool Disposed { get; private set; }
    /// <summary>Submit serial of the last command list that used this resource (lifetime validation; 0 = never).</summary>
    internal long LastUseSerial;
    protected abstract void Destroy();
    // vkDestroyDevice implicitly frees ALL child objects, so calling vkDestroyXxx after the device is gone is
    // invalid (VUID-vkDestroy*-device-parameter) AND unnecessary. Skip Destroy() once the device is torn down
    // (covers every resource type / dispose timing centrally; mirrors the VulkanQueue.WaitIdle / VkFence.Wait guards).
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        Dev.Lifetime.CheckDispose(LastUseSerial, "Vulkan resource #" + Handle.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Disposed = true;
        if (Dev.DeviceAlive)
        {
            Destroy();
        }

        Dev.Unregister(Handle);
    }
}

internal sealed unsafe class VkBuffer : VkResource, IBuffer, IMappedBuffer
{
    public Vk.Buffer Buffer; public Vk.DeviceMemory Memory; private void* _mapped;
    public ulong SizeBytes { get; }
    private readonly MemoryClass _mem;
    MemoryClass IBuffer.Memory => _mem;
    public IntPtr MappedPointer => (IntPtr)_mapped;
    public VkBuffer(VulkanDevice dev, in BufferDesc d) : base(dev)
    {
        SizeBytes = d.SizeBytes;
        _mem = d.Memory;
        var ci = new Vk.BufferCreateInfo { SType = Vk.StructureType.BufferCreateInfo, Size = d.SizeBytes, Usage = VulkanMap.BufferUsage(d.Usage), SharingMode = Vk.SharingMode.Exclusive };
        VulkanDevice.Check(dev.Api.CreateBuffer(dev.Device, &ci, null, out Buffer), "CreateBuffer");
        dev.Api.GetBufferMemoryRequirements(dev.Device, Buffer, out Vk.MemoryRequirements req);
        Memory = dev.AllocateMemory(req, VulkanMap.MemoryProps(d.Memory), out bool gpuPoison);
        _ = dev.Api.BindBufferMemory(dev.Device, Buffer, Memory, 0);
        if (gpuPoison)
        {
            dev.PoisonOnGpu(Buffer, default, default);   // memory the host cannot map
        }

        Vk.MemoryPropertyFlags props = VulkanMap.MemoryProps(d.Memory);
        if ((props & Vk.MemoryPropertyFlags.HostVisibleBit) != 0)
        {
            void* p;
            _ = dev.Api.MapMemory(dev.Device, Memory, 0, d.SizeBytes, 0, &p);
            _mapped = p;
        }
    }
    protected override void Destroy()
    {
        if (_mapped != null)
        {
            Dev.Api.UnmapMemory(Dev.Device, Memory);
            _mapped = null;
        }

        Dev.Api.DestroyBuffer(Dev.Device, Buffer, null);
        Dev.Api.FreeMemory(Dev.Device, Memory, null);
    }
}

internal sealed unsafe class VkTexture : VkResource, ITexture
{
    public Vk.Image Image; public Vk.DeviceMemory Memory; public Vk.ImageView DefaultView;
    public TextureDesc Desc { get; }
    /// <summary>The layout a sampled-image descriptor must name for this texture: the layout the most recently
    /// RECORDED barrier left it in (recording is single-threaded, barriers precede the pass that binds). A depth
    /// texture read-only-tested AND sampled sits in DEPTH_STENCIL_READ_ONLY_OPTIMAL (ResourceState.DepthReadShaderResource),
    /// which the descriptor must match (VUID 04150); everything else samples in SHADER_READ_ONLY_OPTIMAL.</summary>
    public Vk.ImageLayout SampledLayout = Vk.ImageLayout.ShaderReadOnlyOptimal;
    public VkTexture(VulkanDevice dev, in TextureDesc d) : base(dev)
    {
        Desc = d;
        var ci = new Vk.ImageCreateInfo
        {
            SType = Vk.StructureType.ImageCreateInfo,
            ImageType = Vk.ImageType.Type2D,
            Format = VulkanMap.Format(d.Format),
            Extent = new Vk.Extent3D(d.Width, d.Height, 1),
            MipLevels = Math.Max(1u, d.MipLevels),
            ArrayLayers = Math.Max(1u, d.DepthOrArrayLayers),
            Samples = (Vk.SampleCountFlags)Math.Max(1u, d.SampleCount),
            Tiling = Vk.ImageTiling.Optimal,
            Usage = VulkanMap.ImageUsage(d.Usage),
            SharingMode = Vk.SharingMode.Exclusive,
            InitialLayout = Vk.ImageLayout.Undefined,
        };
        VulkanDevice.Check(dev.Api.CreateImage(dev.Device, &ci, null, out Image), "CreateImage");
        dev.Api.GetImageMemoryRequirements(dev.Device, Image, out Vk.MemoryRequirements req);
        Memory = dev.AllocateMemory(req, Vk.MemoryPropertyFlags.DeviceLocalBit, out bool gpuPoison);
        _ = dev.Api.BindImageMemory(dev.Device, Image, Memory, 0);
        if (gpuPoison)
        {
            dev.PoisonOnGpu(default, Image, d);   // memory the host cannot map
        }

        var vci = new Vk.ImageViewCreateInfo
        {
            SType = Vk.StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = Vk.ImageViewType.Type2D,
            Format = VulkanMap.Format(d.Format),
            SubresourceRange = new Vk.ImageSubresourceRange(VulkanMap.Aspect(d.Format), 0, Math.Max(1u, d.MipLevels), 0, Math.Max(1u, d.DepthOrArrayLayers)),
        };
        VulkanDevice.Check(dev.Api.CreateImageView(dev.Device, &vci, null, out DefaultView), "CreateImageView");
    }
    // Wrap an externally-owned image (a swapchain backbuffer): no memory, no owner.
    public VkTexture(VulkanDevice dev, Vk.Image image, in TextureDesc d) : base(dev)
    {
        Desc = d;
        Image = image;
        var vci = new Vk.ImageViewCreateInfo { SType = Vk.StructureType.ImageViewCreateInfo, Image = image, ViewType = Vk.ImageViewType.Type2D, Format = VulkanMap.Format(d.Format), SubresourceRange = new Vk.ImageSubresourceRange(VulkanMap.Aspect(d.Format), 0, 1, 0, 1) };
        _ = dev.Api.CreateImageView(dev.Device, &vci, null, out DefaultView);
    }
    protected override void Destroy()
    {
        Dev.Api.DestroyImageView(Dev.Device, DefaultView, null);
        if (Memory.Handle != 0)
        {
            Dev.Api.DestroyImage(Dev.Device, Image, null);
            Dev.Api.FreeMemory(Dev.Device, Memory, null);
        }
    }
}

internal sealed class VkBufferViewRes : VkResource, IBufferView
{
    public GpuBufferView View { get; }
    public VkBufferViewRes(VulkanDevice dev, IBuffer b, in BufferViewDesc d) : base(dev)
    {
        View = new GpuBufferView(b.Handle, d.Offset, d.SizeBytes == 0 ? b.SizeBytes : d.SizeBytes);
    }

    protected override void Destroy() { }
}
internal sealed unsafe class VkTextureViewRes : VkResource, ITextureView
{
    public Vk.ImageView View;
    public readonly VkTexture Texture;
    public VkTextureViewRes(VulkanDevice dev, VkTexture tex, in TextureViewDesc d) : base(dev)
    {
        Texture = tex;
        uint baseMip = d.BaseMip, mipCount = Math.Max(1u, d.MipCount == 0 ? Math.Max(1u, tex.Desc.MipLevels) : d.MipCount);
        uint baseLayer = d.BaseArrayLayer, layerCount = Math.Max(1u, d.ArrayLayerCount == 0 ? Math.Max(1u, tex.Desc.DepthOrArrayLayers) : d.ArrayLayerCount);
        Format fmt = d.Format == Format.Unknown ? tex.Desc.Format : d.Format;
        var vci = new Vk.ImageViewCreateInfo
        {
            SType = Vk.StructureType.ImageViewCreateInfo,
            Image = tex.Image,
            ViewType = (d.Dimension == TextureDimension.Tex2DArray || layerCount > 1) ? Vk.ImageViewType.Type2DArray : Vk.ImageViewType.Type2D,
            Format = VulkanMap.Format(fmt),
            SubresourceRange = new Vk.ImageSubresourceRange(VulkanMap.Aspect(fmt), baseMip, mipCount, baseLayer, layerCount),
        };
        VulkanDevice.Check(dev.Api.CreateImageView(dev.Device, &vci, null, out View), "CreateImageView(SRV)");
    }
    protected override void Destroy()
    {
        if (View.Handle != 0)
        {
            Dev.Api.DestroyImageView(Dev.Device, View, null);
        }
    }
}

internal sealed unsafe class VkSampler : VkResource, ISampler
{
    public Vk.Sampler Sampler;
    public VkSampler(VulkanDevice dev, in SamplerDesc d) : base(dev)
    {
        Vk.SamplerAddressMode addr = d.Address switch { SamplerAddressMode.ClampToEdge => Vk.SamplerAddressMode.ClampToEdge, SamplerAddressMode.MirrorRepeat => Vk.SamplerAddressMode.MirroredRepeat, _ => Vk.SamplerAddressMode.Repeat };
        Vk.Filter filt = d.Filter == SamplerFilter.Nearest ? Vk.Filter.Nearest : Vk.Filter.Linear;
        var ci = new Vk.SamplerCreateInfo
        {
            SType = Vk.StructureType.SamplerCreateInfo,
            MagFilter = filt,
            MinFilter = filt,
            MipmapMode = d.Filter == SamplerFilter.Nearest ? Vk.SamplerMipmapMode.Nearest : Vk.SamplerMipmapMode.Linear,
            AddressModeU = addr,
            AddressModeV = addr,
            AddressModeW = addr,
            AnisotropyEnable = d.Filter == SamplerFilter.Anisotropic,
            MaxAnisotropy = Math.Max(1f, d.MaxAnisotropy),
            MinLod = 0f,
            MaxLod = Vk.Vk.LodClampNone,
            CompareEnable = d.Comparison,
            CompareOp = d.Comparison ? Vk.CompareOp.GreaterOrEqual : Vk.CompareOp.Always,   // reversed-Z PCF
        };
        VulkanDevice.Check(dev.Api.CreateSampler(dev.Device, &ci, null, out Sampler), "CreateSampler");
    }
    protected override void Destroy()
    {
        Dev.Api.DestroySampler(Dev.Device, Sampler, null);
    }
}

internal sealed unsafe class VkFence : VkResource, IFence
{
    public Vk.Semaphore Timeline;
    public VkFence(VulkanDevice dev, ulong initial) : base(dev)
    {
        var tci = new Vk.SemaphoreTypeCreateInfo { SType = Vk.StructureType.SemaphoreTypeCreateInfo, SemaphoreType = Vk.SemaphoreType.Timeline, InitialValue = initial };
        var ci = new Vk.SemaphoreCreateInfo { SType = Vk.StructureType.SemaphoreCreateInfo, PNext = &tci };
        VulkanDevice.Check(dev.Api.CreateSemaphore(dev.Device, &ci, null, out Timeline), "CreateTimelineSemaphore");
    }
    // Guard teardown reads against an already-destroyed device (mirrors VulkanQueue.WaitIdle): on quit the GPU is
    // drained first, so if the device is gone here there is no in-flight work to observe -> report complete / no-op.
    // Without this, GpuScheduler.Dispose's frame-fence waits hit vkWaitSemaphores with an invalid VkDevice.
    public ulong CompletedValue
    {
        get
        {
            if (!Dev.DeviceAlive)
            {
                return ulong.MaxValue;
            }

            ulong v;
            _ = Dev.Api.GetSemaphoreCounterValue(Dev.Device, Timeline, &v);
            return v;
        }
    }
    public void Wait(ulong value)
    {
        if (!Dev.DeviceAlive)
        {
            return;
        }

        Vk.Semaphore sem = Timeline;
        ulong val = value;
        var wi = new Vk.SemaphoreWaitInfo { SType = Vk.StructureType.SemaphoreWaitInfo, SemaphoreCount = 1, PSemaphores = &sem, PValues = &val };
        _ = Dev.Api.WaitSemaphores(Dev.Device, &wi, ulong.MaxValue);
        if (value >= _signalValue)
        {
            Dev.Lifetime.OnCompleted(_signalSerial);   // the submit that signalled it is done
        }
    }
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
    protected override void Destroy()
    {
        Dev.Api.DestroySemaphore(Dev.Device, Timeline, null);
    }
}

internal sealed unsafe class VkQueryHeap : VkResource, IQueryHeap
{
    public Vk.QueryPool Pool;
    public int RangeStart => 0;
    public int Capacity { get; }
    public VkQueryHeap(VulkanDevice dev, int capacity) : base(dev)
    {
        Capacity = capacity;
        var ci = new Vk.QueryPoolCreateInfo { SType = Vk.StructureType.QueryPoolCreateInfo, QueryType = Vk.QueryType.Timestamp, QueryCount = (uint)capacity };
        VulkanDevice.Check(dev.Api.CreateQueryPool(dev.Device, &ci, null, out Pool), "CreateQueryPool");
        dev.Api.ResetQueryPool(dev.Device, Pool, 0, (uint)capacity);
    }
    public void ResolveTo(IBuffer dst)
    {
        if (dst is VkBuffer vb)
        {
            _ = Dev.Api.GetQueryPoolResults(Dev.Device, Pool, 0, (uint)Capacity, (nuint)(Capacity * sizeof(ulong)), (void*)vb.MappedPointer, sizeof(ulong), Vk.QueryResultFlags.Result64Bit);
        }
    }
    /// <summary>Host-reset a query sub-range so it can be re-written this frame (needs HostQueryReset; enabled in Init).</summary>
    public void ResetRange(int start, int count)
    {
        Dev.Api.ResetQueryPool(Dev.Device, Pool, (uint)start, (uint)count);
    }

    /// <summary>Blocking read of a resolved timestamp sub-range (call AFTER the frame fence). false if not ready.</summary>
    public bool ReadRange(int start, int count, Span<ulong> dst)
    {
        fixed (ulong* p = dst)
        {
            return Dev.Api.GetQueryPoolResults(Dev.Device, Pool, (uint)start, (uint)count, (nuint)(count * sizeof(ulong)), p, sizeof(ulong), Vk.QueryResultFlags.Result64Bit | Vk.QueryResultFlags.ResultWaitBit) == Vk.Result.Success;
        }
    }
    protected override void Destroy()
    {
        Dev.Api.DestroyQueryPool(Dev.Device, Pool, null);
    }
}

internal sealed unsafe class VkDescriptorHeap : VkResource, IDescriptorHeap
{
    public Vk.DescriptorPool Pool;
    public DescriptorKind Kind { get; }
    private int _used; private readonly int _cap;
    public VkDescriptorHeap(VulkanDevice dev, DescriptorKind kind, int capacity) : base(dev)
    {
        Kind = kind;
        _cap = capacity;
        var size = new Vk.DescriptorPoolSize { Type = kind == DescriptorKind.Sampler ? Vk.DescriptorType.Sampler : Vk.DescriptorType.StorageBuffer, DescriptorCount = (uint)capacity };
        var ci = new Vk.DescriptorPoolCreateInfo { SType = Vk.StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)capacity, PoolSizeCount = 1, PPoolSizes = &size, Flags = Vk.DescriptorPoolCreateFlags.FreeDescriptorSetBit };
        VulkanDevice.Check(dev.Api.CreateDescriptorPool(dev.Device, &ci, null, out Pool), "CreateDescriptorPool");
    }
    public DescriptorRange Allocate(int count)
    {
        int start = _used;
        _used += count;
        return _used > _cap
            ? throw new InvalidOperationException("Vulkan descriptor heap exhausted.")
            : new DescriptorRange(Handle, start, count);
    }
    protected override void Destroy()
    {
        Dev.Api.DestroyDescriptorPool(Dev.Device, Pool, null);
    }
}

internal sealed unsafe class VkPipelineLayout : VkResource, IPipelineLayout
{
    public Vk.PipelineLayout Layout; public Vk.DescriptorSetLayout SetLayout;
    // Binding plan in set 0: [CBV][bufSRV][UAV][texSRV][sampler], each a single-descriptor binding.
    public readonly int CbvCount, SrvCount, UavCount, TexSrvCount, SamplerCount;
    public const int CbvBase = 0;
    public int SrvBase => CbvCount;
    public int UavBase => CbvCount + SrvCount;
    public int TexBase => CbvCount + SrvCount + UavCount;
    public int SamplerBase => CbvCount + SrvCount + UavCount + TexSrvCount;
    public int Total => CbvCount + SrvCount + UavCount + TexSrvCount + SamplerCount;
    public VkPipelineLayout(VulkanDevice dev, in PipelineLayoutDesc d) : base(dev)
    {
        CbvCount = d.CbvCount;
        SrvCount = d.SrvCount;
        UavCount = d.UavCount;
        TexSrvCount = d.TextureSrvCount;
        SamplerCount = d.SamplerCount;
        var bindings = new List<Vk.DescriptorSetLayoutBinding>();
        int b = 0;
        for (int i = 0; i < d.CbvCount; i++)
        {
            bindings.Add(Binding(b++, Vk.DescriptorType.UniformBuffer));
        }

        for (int i = 0; i < d.SrvCount; i++)
        {
            bindings.Add(Binding(b++, Vk.DescriptorType.StorageBuffer));
        }

        for (int i = 0; i < d.UavCount; i++)
        {
            bindings.Add(Binding(b++, Vk.DescriptorType.StorageBuffer));
        }

        for (int i = 0; i < d.TextureSrvCount; i++)
        {
            bindings.Add(Binding(b++, Vk.DescriptorType.SampledImage));
        }

        for (int i = 0; i < d.SamplerCount; i++)
        {
            bindings.Add(Binding(b++, Vk.DescriptorType.Sampler));
        }

        Vk.DescriptorSetLayoutBinding[] arr = [.. bindings];
        fixed (Vk.DescriptorSetLayoutBinding* pb = arr)
        {
            var slci = new Vk.DescriptorSetLayoutCreateInfo { SType = Vk.StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)arr.Length, PBindings = pb };
            VulkanDevice.Check(dev.Api.CreateDescriptorSetLayout(dev.Device, &slci, null, out SetLayout), "CreateDescriptorSetLayout");
        }

        Vk.DescriptorSetLayout setL = SetLayout;
        var pcr = new Vk.PushConstantRange { StageFlags = Vk.ShaderStageFlags.All, Offset = 0, Size = (uint)(d.RootConstants32 * 4) };
        var plci = new Vk.PipelineLayoutCreateInfo { SType = Vk.StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setL, PushConstantRangeCount = (uint)(d.RootConstants32 > 0 ? 1 : 0), PPushConstantRanges = &pcr };
        VulkanDevice.Check(dev.Api.CreatePipelineLayout(dev.Device, &plci, null, out Layout), "CreatePipelineLayout");
    }
    private static Vk.DescriptorSetLayoutBinding Binding(int b, Vk.DescriptorType t)
    {
        return new() { Binding = (uint)b, DescriptorType = t, DescriptorCount = 1, StageFlags = Vk.ShaderStageFlags.All };
    }

    protected override void Destroy()
    {
        Dev.Api.DestroyPipelineLayout(Dev.Device, Layout, null);
        Dev.Api.DestroyDescriptorSetLayout(Dev.Device, SetLayout, null);
    }
}

internal sealed unsafe class VkComputePipeline : VkResource, IComputePipeline
{
    public Vk.Pipeline Pipeline; public string Name { get; }
    public VkPipelineLayout LayoutObj { get; private set; } = null!;
    public VkComputePipeline(VulkanDevice dev, in ComputePipelineDesc d) : base(dev)
    {
        Name = d.Name;
        var layout = (VkPipelineLayout)d.Layout;
        LayoutObj = layout;
        Vk.ShaderModule module = dev.CreateShaderModule(d.ByteCode);
        byte* entry = (byte*)SilkMarshal.StringToPtr(string.IsNullOrEmpty(d.EntryPoint) ? "CSMain" : d.EntryPoint);
        var stage = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureType.PipelineShaderStageCreateInfo, Stage = Vk.ShaderStageFlags.ComputeBit, Module = module, PName = entry };
        var ci = new Vk.ComputePipelineCreateInfo { SType = Vk.StructureType.ComputePipelineCreateInfo, Stage = stage, Layout = layout.Layout };
        VulkanDevice.Check(dev.Api.CreateComputePipelines(dev.Device, dev.PsoCache, 1, &ci, null, out Pipeline), "CreateComputePipeline");
        dev.Api.DestroyShaderModule(dev.Device, module, null);
        _ = SilkMarshal.Free((nint)entry);
    }
    protected override void Destroy()
    {
        Dev.Api.DestroyPipeline(Dev.Device, Pipeline, null);
    }
}

internal sealed unsafe class VkRenderPipeline : VkResource, IRenderPipeline
{
    public Vk.Pipeline Pipeline; public string Name { get; }
    public Vk.PipelineLayout LayoutHandle { get; private set; }
    public VkPipelineLayout LayoutObj { get; private set; } = null!;
    // Full graphics PSO: dynamic rendering (no render-pass object), reversed-Z GREATER depth, CCW front.
    public VkRenderPipeline(VulkanDevice dev, in RenderPipelineDesc d) : base(dev)
    {
        Name = d.Name;
        var layout = (VkPipelineLayout)d.Layout;
        LayoutHandle = layout.Layout;
        LayoutObj = layout;
        bool hasPs = d.PixelShader != null && d.PixelShader.Length > 0;   // VS-only (depth-prepass / shadow) => 1 stage
        Vk.ShaderModule vsMod = dev.CreateShaderModule(d.VertexShader);
        Vk.ShaderModule psMod = default;
        if (hasPs)
        {
            psMod = dev.CreateShaderModule(d.PixelShader!);
        }

        byte* vsEntry = (byte*)SilkMarshal.StringToPtr(string.IsNullOrEmpty(d.VertexEntry) ? "VSMain" : d.VertexEntry);
        byte* psEntry = (byte*)SilkMarshal.StringToPtr(string.IsNullOrEmpty(d.PixelEntry) ? "PSMain" : d.PixelEntry);
        uint stageCount = hasPs ? 2u : 1u;
        Vk.PipelineShaderStageCreateInfo* stages = stackalloc Vk.PipelineShaderStageCreateInfo[2];
        stages[0] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureType.PipelineShaderStageCreateInfo, Stage = Vk.ShaderStageFlags.VertexBit, Module = vsMod, PName = vsEntry };
        if (hasPs)
        {
            stages[1] = new Vk.PipelineShaderStageCreateInfo { SType = Vk.StructureType.PipelineShaderStageCreateInfo, Stage = Vk.ShaderStageFlags.FragmentBit, Module = psMod, PName = psEntry };
        }

        VertexAttribute[] attrs = d.VertexAttributes ?? [];
        Vk.VertexInputAttributeDescription* viAttrs = stackalloc Vk.VertexInputAttributeDescription[attrs.Length == 0 ? 1 : attrs.Length];
        for (int i = 0; i < attrs.Length; i++)
        {
            viAttrs[i] = new Vk.VertexInputAttributeDescription { Location = (uint)attrs[i].Location, Binding = 0, Format = VulkanMap.Format(attrs[i].Format), Offset = (uint)attrs[i].Offset };
        }

        var binding = new Vk.VertexInputBindingDescription { Binding = 0, Stride = (uint)d.VertexStride, InputRate = Vk.VertexInputRate.Vertex };
        var vi = new Vk.PipelineVertexInputStateCreateInfo
        {
            SType = Vk.StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = (uint)(d.VertexStride > 0 ? 1 : 0),
            PVertexBindingDescriptions = d.VertexStride > 0 ? &binding : null,
            VertexAttributeDescriptionCount = (uint)attrs.Length,
            PVertexAttributeDescriptions = attrs.Length == 0 ? null : viAttrs,
        };
        var ia = new Vk.PipelineInputAssemblyStateCreateInfo { SType = Vk.StructureType.PipelineInputAssemblyStateCreateInfo, Topology = d.Topology switch { PrimitiveTopology.TriangleStrip => Vk.PrimitiveTopology.TriangleStrip, PrimitiveTopology.LineList => Vk.PrimitiveTopology.LineList, PrimitiveTopology.PointList => Vk.PrimitiveTopology.PointList, _ => Vk.PrimitiveTopology.TriangleList } };
        var vp = new Vk.PipelineViewportStateCreateInfo { SType = Vk.StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
        var rs = new Vk.PipelineRasterizationStateCreateInfo
        {
            SType = Vk.StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = d.Wireframe && dev.FillModeNonSolid ? Vk.PolygonMode.Line : Vk.PolygonMode.Fill,
            LineWidth = 1f,
            CullMode = d.Cull switch { CullMode.Front => Vk.CullModeFlags.FrontBit, CullMode.Back => Vk.CullModeFlags.BackBit, _ => Vk.CullModeFlags.None },
            FrontFace = Vk.FrontFace.CounterClockwise,   // CCW = front
        };
        var ms = new Vk.PipelineMultisampleStateCreateInfo { SType = Vk.StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = (Vk.SampleCountFlags)Math.Max(1, d.SampleCount) };
        var ds = new Vk.PipelineDepthStencilStateCreateInfo
        {
            SType = Vk.StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = d.DepthTest,
            DepthWriteEnable = d.DepthWrite,
            DepthCompareOp = d.DepthCompare switch { DepthCompare.GreaterEqual => Vk.CompareOp.GreaterOrEqual, DepthCompare.Equal => Vk.CompareOp.Equal, DepthCompare.LessEqual => Vk.CompareOp.LessOrEqual, DepthCompare.Always => Vk.CompareOp.Always, _ => Vk.CompareOp.Greater },   // reversed-Z default
        };
        if (d.StencilEnable)   // NanoVG stencil-then-cover (off by default; the reference value is dynamic)
        {
            ds.StencilTestEnable = true;
            ds.Front = VkStencilOpState(d.StencilFront, d.StencilReadMask, d.StencilWriteMask);
            ds.Back = VkStencilOpState(d.StencilBack, d.StencilReadMask, d.StencilWriteMask);
        }

        int colorCount = d.ColorFormats?.Length ?? 0;
        Vk.PipelineColorBlendAttachmentState* blends = stackalloc Vk.PipelineColorBlendAttachmentState[colorCount == 0 ? 1 : colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            bool en = d.Blend != BlendMode.Opaque;
            blends[i] = new Vk.PipelineColorBlendAttachmentState
            {
                BlendEnable = en,
                ColorWriteMask = d.ColorWriteDisable ? 0 : (Vk.ColorComponentFlags.RBit | Vk.ColorComponentFlags.GBit | Vk.ColorComponentFlags.BBit | Vk.ColorComponentFlags.ABit),
                SrcColorBlendFactor = d.Blend == BlendMode.Multiplicative ? Vk.BlendFactor.Zero : (d.Blend is BlendMode.Additive or BlendMode.PremultipliedAlpha) ? Vk.BlendFactor.One : Vk.BlendFactor.SrcAlpha,
                DstColorBlendFactor = d.Blend == BlendMode.Multiplicative ? Vk.BlendFactor.SrcColor : d.Blend == BlendMode.Additive ? Vk.BlendFactor.One : Vk.BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = Vk.BlendOp.Add,
                // Multiplicative: dst *= src, alpha {Zero, One} keeps the target alpha (matches Dx12BlendState).
                SrcAlphaBlendFactor = d.Blend == BlendMode.Multiplicative ? Vk.BlendFactor.Zero : Vk.BlendFactor.One,
                DstAlphaBlendFactor = d.Blend == BlendMode.Multiplicative ? Vk.BlendFactor.One : d.Blend == BlendMode.PremultipliedAlpha ? Vk.BlendFactor.OneMinusSrcAlpha : Vk.BlendFactor.Zero,
                AlphaBlendOp = Vk.BlendOp.Add,
            };
        }

        var cb = new Vk.PipelineColorBlendStateCreateInfo { SType = Vk.StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = (uint)colorCount, PAttachments = colorCount == 0 ? null : blends };
        Vk.DynamicState* dyn = stackalloc Vk.DynamicState[3];
        dyn[0] = Vk.DynamicState.Viewport;
        dyn[1] = Vk.DynamicState.Scissor;
        uint dynCount = 2;
        if (d.StencilEnable)
        {
            dyn[2] = Vk.DynamicState.StencilReference;
            dynCount = 3;
        }   // dynamic stencil reference

        var dynState = new Vk.PipelineDynamicStateCreateInfo { SType = Vk.StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = dynCount, PDynamicStates = dyn };

        Vk.Format* colorFmts = stackalloc Vk.Format[colorCount == 0 ? 1 : colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            colorFmts[i] = VulkanMap.Format(d.ColorFormats![i]);
        }

        var rendering = new Vk.PipelineRenderingCreateInfo
        {
            SType = Vk.StructureType.PipelineRenderingCreateInfo,
            ColorAttachmentCount = (uint)colorCount,
            PColorAttachmentFormats = colorCount == 0 ? null : colorFmts,
            DepthAttachmentFormat = d.DepthFormat == Format.Unknown ? Vk.Format.Undefined : VulkanMap.Format(d.DepthFormat),
            // The stencil format follows the attachment the pass binds, not whether this pipeline tests it: with a
            // D24S8 attachment bound, a pipeline that leaves the stencil alone still has to name the format (VUID
            // vkCmdDraw-dynamicRenderingUnusedAttachments-08917 otherwise, once per draw).
            StencilAttachmentFormat = d.StencilFormat != Format.Unknown
                ? VulkanMap.Format(d.StencilFormat)
                : (d.DepthFormat != Format.Unknown && VulkanMap.HasStencil(d.DepthFormat) ? VulkanMap.Format(d.DepthFormat) : Vk.Format.Undefined),
        };
        var ci = new Vk.GraphicsPipelineCreateInfo
        {
            SType = Vk.StructureType.GraphicsPipelineCreateInfo,
            PNext = &rendering,
            StageCount = stageCount,
            PStages = stages,
            PVertexInputState = &vi,
            PInputAssemblyState = &ia,
            PViewportState = &vp,
            PRasterizationState = &rs,
            PMultisampleState = &ms,
            PDepthStencilState = &ds,
            PColorBlendState = &cb,
            PDynamicState = &dynState,
            Layout = layout.Layout,
        };
        VulkanDevice.Check(dev.Api.CreateGraphicsPipelines(dev.Device, dev.PsoCache, 1, &ci, null, out Pipeline), "CreateGraphicsPipeline[" + (d.Name ?? "?") + "]");
        dev.Api.DestroyShaderModule(dev.Device, vsMod, null);
        if (hasPs)
        {
            dev.Api.DestroyShaderModule(dev.Device, psMod, null);
        }

        _ = SilkMarshal.Free((nint)vsEntry);
        _ = SilkMarshal.Free((nint)psEntry);
    }
    private static Vk.StencilOpState VkStencilOpState(StencilFaceState f, byte readMask, byte writeMask)
    {
        return new()
        {
            CompareOp = f.Compare switch { StencilCompare.Equal => Vk.CompareOp.Equal, StencilCompare.NotEqual => Vk.CompareOp.NotEqual, StencilCompare.Never => Vk.CompareOp.Never, _ => Vk.CompareOp.Always },
            FailOp = VkStencilOp(f.Fail),
            DepthFailOp = VkStencilOp(f.DepthFail),
            PassOp = VkStencilOp(f.Pass),
            CompareMask = readMask,
            WriteMask = writeMask,
            Reference = 0,   // reference is dynamic (CmdSetStencilReference)
        };
    }

    private static Vk.StencilOp VkStencilOp(StencilOp o)
    {
        return o switch
        {
            StencilOp.Zero => Vk.StencilOp.Zero,
            StencilOp.Replace => Vk.StencilOp.Replace,
            StencilOp.IncrementWrap => Vk.StencilOp.IncrementAndWrap,
            StencilOp.DecrementWrap => Vk.StencilOp.DecrementAndWrap,
            StencilOp.Invert => Vk.StencilOp.Invert,
            _ => Vk.StencilOp.Keep,
        };
    }

    protected override void Destroy()
    {
        if (Pipeline.Handle != 0)
        {
            Dev.Api.DestroyPipeline(Dev.Device, Pipeline, null);
        }
    }
}

internal sealed unsafe class VulkanCommandList : ICommandList, IGraphicsRecorder
{
    private readonly VulkanDevice _dev;
    public Vk.CommandBuffer Cmd;
    public QueueKind Queue { get; }
    private VkPipelineLayout? _boundLayout; private bool _descDirty;
    private readonly GpuBufferView[] _cbv = new GpuBufferView[8];
    private readonly GpuBufferView[] _srvBuf = new GpuBufferView[16];
    private readonly GpuBufferView[] _uav = new GpuBufferView[16];
    private readonly Vk.ImageView[] _tex = new Vk.ImageView[16];
    private readonly VkTexture?[] _texRes = new VkTexture?[16];
    private readonly Vk.Sampler[] _samp = new Vk.Sampler[16];
    public VulkanCommandList(VulkanDevice dev, Vk.CommandBuffer cmd, QueueKind queue)
    {
        _dev = dev;
        Cmd = cmd;
        Queue = queue;
    }

    public void Begin()
    {
        _ = _dev.Api.ResetCommandBuffer(Cmd, 0);
        var bi = new Vk.CommandBufferBeginInfo { SType = Vk.StructureType.CommandBufferBeginInfo, Flags = Vk.CommandBufferUsageFlags.OneTimeSubmitBit };
        _ = _dev.Api.BeginCommandBuffer(Cmd, &bi);
    }
    public void Close()
    {
        _dev.Api.EndCommandBuffer(Cmd);
    }

    public void ResourceBarrier(ReadOnlySpan<ResourceBarrierDesc> barriers)
    {
        foreach (ResourceBarrierDesc b in barriers)
        {
            VkResource? res = _dev.Lookup(b.ResourceHandle);
            if (res is VkTexture tex)
            {
                (Vk.ImageLayout oldL, Vk.AccessFlags2 srcA, Vk.PipelineStageFlags2 srcS) = b.IsUavBarrier ? (Vk.ImageLayout.General, Vk.AccessFlags2.ShaderStorageWriteBit, Vk.PipelineStageFlags2.ComputeShaderBit) : VulkanMap.ImageState(b.Before);
                (Vk.ImageLayout newL, Vk.AccessFlags2 dstA, Vk.PipelineStageFlags2 dstS) = b.IsUavBarrier ? (Vk.ImageLayout.General, Vk.AccessFlags2.ShaderStorageReadBit, Vk.PipelineStageFlags2.ComputeShaderBit) : VulkanMap.ImageState(b.After);
                // A transition TO UNDEFINED is illegal (VUID-VkImageMemoryBarrier2-newLayout-01198) and meaningless —
                // it only arises from the graph's terminal transient state-reset to Common (Common maps to UNDEFINED
                // for images). Skip the GPU barrier; the CPU-side graph state tracking is unaffected, and the next
                // frame's first-use barrier legitimately uses oldLayout=UNDEFINED (discard) for the transient.
                if (!b.IsUavBarrier && newL == Vk.ImageLayout.Undefined)
                {
                    continue;
                }

                if (newL is Vk.ImageLayout.ShaderReadOnlyOptimal or Vk.ImageLayout.DepthStencilReadOnlyOptimal or Vk.ImageLayout.General)
                {
                    tex.SampledLayout = newL;
                }

                var imb = new Vk.ImageMemoryBarrier2
                {
                    SType = Vk.StructureType.ImageMemoryBarrier2,
                    Image = tex.Image,
                    OldLayout = oldL,
                    NewLayout = newL,
                    SrcStageMask = srcS,
                    SrcAccessMask = srcA,
                    DstStageMask = dstS,
                    DstAccessMask = dstA,
                    SrcQueueFamilyIndex = Vk.Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.Vk.QueueFamilyIgnored,
                    SubresourceRange = new Vk.ImageSubresourceRange(VulkanMap.Aspect(tex.Desc.Format), 0, Math.Max(1u, tex.Desc.MipLevels), 0, Math.Max(1u, tex.Desc.DepthOrArrayLayers)),
                };
                var dep = new Vk.DependencyInfo { SType = Vk.StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &imb };
                _dev.Api.CmdPipelineBarrier2(Cmd, &dep);
            }
            else if (res is VkBuffer buf)
            {
                (Vk.AccessFlags2 srcA, Vk.PipelineStageFlags2 srcS) = VulkanMap.BufferState(b.Before);
                (Vk.AccessFlags2 dstA, Vk.PipelineStageFlags2 dstS) = VulkanMap.BufferState(b.After);
                var bmb = new Vk.BufferMemoryBarrier2
                {
                    SType = Vk.StructureType.BufferMemoryBarrier2,
                    Buffer = buf.Buffer,
                    Offset = 0,
                    Size = buf.SizeBytes,
                    SrcStageMask = srcS,
                    SrcAccessMask = srcA,
                    DstStageMask = dstS,
                    DstAccessMask = dstA,
                    SrcQueueFamilyIndex = Vk.Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.Vk.QueueFamilyIgnored,
                };
                var dep = new Vk.DependencyInfo { SType = Vk.StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &bmb };
                _dev.Api.CmdPipelineBarrier2(Cmd, &dep);
            }
        }
    }

    public void WriteTimestamp(IQueryHeap heap, int index)
    {
        if (heap is VkQueryHeap qh)
        {
            _dev.Api.CmdWriteTimestamp2(Cmd, Vk.PipelineStageFlags2.BottomOfPipeBit, qh.Pool, (uint)index);
        }
    }

    // ---- IGraphicsRecorder (dynamic rendering) ----
    public void BeginRendering(ReadOnlySpan<ColorAttachment> colors, in DepthAttachment depth, uint width, uint height)
    {
        Span<Vk.RenderingAttachmentInfo> ca = stackalloc Vk.RenderingAttachmentInfo[colors.Length == 0 ? 1 : colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            var t = (VkTexture)_dev.Lookup(colors[i].TextureHandle)!;
            ca[i] = new Vk.RenderingAttachmentInfo
            {
                SType = Vk.StructureType.RenderingAttachmentInfo,
                ImageView = t.DefaultView,
                ImageLayout = Vk.ImageLayout.ColorAttachmentOptimal,
                LoadOp = colors[i].Clear ? Vk.AttachmentLoadOp.Clear : Vk.AttachmentLoadOp.Load,
                StoreOp = Vk.AttachmentStoreOp.Store,
                ClearValue = new Vk.ClearValue(new Vk.ClearColorValue(colors[i].ClearValue.R, colors[i].ClearValue.G, colors[i].ClearValue.B, colors[i].ClearValue.A)),
            };
        }

        Vk.RenderingAttachmentInfo da = default, sa = default;
        bool hasDepth = depth.HasDepth;
        bool hasStencil = false;
        if (hasDepth)
        {
            var dt = (VkTexture)_dev.Lookup(depth.TextureHandle)!;
            Vk.ImageLayout layout = depth.ReadOnly ? Vk.ImageLayout.DepthStencilReadOnlyOptimal : Vk.ImageLayout.DepthStencilAttachmentOptimal;
            da = new Vk.RenderingAttachmentInfo
            {
                SType = Vk.StructureType.RenderingAttachmentInfo,
                ImageView = dt.DefaultView,
                ImageLayout = layout,
                LoadOp = depth.Clear ? Vk.AttachmentLoadOp.Clear : Vk.AttachmentLoadOp.Load,
                StoreOp = depth.ReadOnly ? Vk.AttachmentStoreOp.None : Vk.AttachmentStoreOp.Store,
                ClearValue = new Vk.ClearValue { DepthStencil = new Vk.ClearDepthStencilValue(depth.ClearDepth, depth.ClearStencil) },
            };
            // A D24S8 depth needs a SEPARATE stencil attachment in dynamic rendering — without PStencilAttachment the
            // stencil aspect is not bound, so stencil-then-cover / path-clip are undefined on real HW (lavapipe
            // tolerated it). The DefaultView already carries the stencil aspect (VulkanMap.Aspect).
            hasStencil = VulkanMap.HasStencil(dt.Desc.Format);
            if (hasStencil)
            {
                sa = new Vk.RenderingAttachmentInfo
                {
                    SType = Vk.StructureType.RenderingAttachmentInfo,
                    ImageView = dt.DefaultView,
                    ImageLayout = layout,
                    LoadOp = depth.ClearStencilOnLoad ? Vk.AttachmentLoadOp.Clear : Vk.AttachmentLoadOp.Load,
                    StoreOp = depth.ReadOnly ? Vk.AttachmentStoreOp.None : Vk.AttachmentStoreOp.Store,
                    ClearValue = new Vk.ClearValue { DepthStencil = new Vk.ClearDepthStencilValue(depth.ClearDepth, depth.ClearStencil) },
                };
            }
        }

        fixed (Vk.RenderingAttachmentInfo* pca = ca)
        {
            var ri = new Vk.RenderingInfo
            {
                SType = Vk.StructureType.RenderingInfo,
                RenderArea = new Vk.Rect2D(new Vk.Offset2D(0, 0), new Vk.Extent2D(width, height)),
                LayerCount = 1,
                ColorAttachmentCount = (uint)colors.Length,
                PColorAttachments = colors.Length == 0 ? null : pca,
                PDepthAttachment = hasDepth ? &da : null,
                PStencilAttachment = hasStencil ? &sa : null,
            };
            _dev.Api.CmdBeginRendering(Cmd, &ri);
        }
    }
    public void EndRendering()
    {
        _dev.Api.CmdEndRendering(Cmd);
    }

    public void SetPipeline(IRenderPipeline pso)
    {
        if (pso is VkRenderPipeline p && p.Pipeline.Handle != 0)
        {
            _dev.BoundLayout = p.LayoutHandle;
            _boundLayout = p.LayoutObj;
            _descDirty = true;
            _dev.Api.CmdBindPipeline(Cmd, Vk.PipelineBindPoint.Graphics, p.Pipeline);
        }
    }
    public void SetViewportScissor(float x, float y, float w, float h, float minDepth = 0f, float maxDepth = 1f)
    {
        var vp = new Vk.Viewport(x, y, w, h, minDepth, maxDepth);
        _dev.Api.CmdSetViewport(Cmd, 0, 1, &vp);
        var sc = new Vk.Rect2D(new Vk.Offset2D((int)x, (int)y), new Vk.Extent2D((uint)w, (uint)h));
        _dev.Api.CmdSetScissor(Cmd, 0, 1, &sc);
    }
    public void SetScissor(int x, int y, uint width, uint height)
    {
        var sc = new Vk.Rect2D(new Vk.Offset2D(x, y), new Vk.Extent2D(width, height));
        _dev.Api.CmdSetScissor(Cmd, 0, 1, &sc);
    }
    public void SetStencilReference(uint reference)
    {
        _dev.Api.CmdSetStencilReference(Cmd, Vk.StencilFaceFlags.FrontAndBack, reference);
    }

    public void BindDescriptorTable(int rootSlot, DescriptorRange range) { /* baseline binds per-resource (below); explicit-table path is optional-tier */ }
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
        var v = (VkTextureViewRes)srv;
        _tex[slot] = v.View;
        _texRes[slot] = v.Texture;
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
        _samp[slot] = ((VkSampler)sampler).Sampler;
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

    private static ulong Fnv(ulong h, ulong v)
    {
        unchecked
        {
            h ^= v;
            h *= 1099511628211UL;
        }

        return h;
    }

    // Lazily build + bind the frame's descriptor set for the bound layout. Sets are cached by
    // (layout, bound-resource tuple) so a stable binding is alloc-free across frames.
    private void FlushDescriptors(Vk.PipelineBindPoint bindPoint)
    {
        VkPipelineLayout? lay = _boundLayout;
        if (lay is null || lay.Total == 0 || !_descDirty)
        {
            return;
        }

        ulong key = Fnv(1469598103934665603UL, lay.SetLayout.Handle);
        for (int i = 0; i < lay.CbvCount; i++)
        {
            key = Fnv(key, _cbv[i].Handle);
            key = Fnv(key, _cbv[i].Offset);
        }

        for (int i = 0; i < lay.SrvCount; i++)
        {
            key = Fnv(key, _srvBuf[i].Handle);
            key = Fnv(key, _srvBuf[i].Offset);
        }

        for (int i = 0; i < lay.UavCount; i++)
        {
            key = Fnv(key, _uav[i].Handle);
            key = Fnv(key, _uav[i].Offset);
        }

        for (int i = 0; i < lay.TexSrvCount; i++)
        {
            key = Fnv(key, _tex[i].Handle);
            key = Fnv(key, (ulong)SampledLayoutOf(i));
        }

        for (int i = 0; i < lay.SamplerCount; i++)
        {
            key = Fnv(key, _samp[i].Handle);
        }

        if (!_dev.DescAlloc.TryGet(key, out Vk.DescriptorSet set))
        {
            set = _dev.DescAlloc.Allocate(lay.SetLayout);
            int nBuf = lay.CbvCount + lay.SrvCount + lay.UavCount;
            int nImg = lay.TexSrvCount + lay.SamplerCount;
            Span<Vk.DescriptorBufferInfo> binfos = stackalloc Vk.DescriptorBufferInfo[nBuf == 0 ? 1 : nBuf];
            Span<Vk.DescriptorImageInfo> iinfos = stackalloc Vk.DescriptorImageInfo[nImg == 0 ? 1 : nImg];
            int bi = 0, ii = 0;
            for (int i = 0; i < lay.CbvCount; i++)
            {
                var b = (VkBuffer)_dev.Lookup(_cbv[i].Handle)!;
                binfos[bi++] = new Vk.DescriptorBufferInfo { Buffer = b.Buffer, Offset = _cbv[i].Offset, Range = _cbv[i].SizeBytes == 0 ? Vk.Vk.WholeSize : _cbv[i].SizeBytes };
            }

            for (int i = 0; i < lay.SrvCount; i++)
            {
                var b = (VkBuffer)_dev.Lookup(_srvBuf[i].Handle)!;
                binfos[bi++] = new Vk.DescriptorBufferInfo { Buffer = b.Buffer, Offset = _srvBuf[i].Offset, Range = _srvBuf[i].SizeBytes == 0 ? Vk.Vk.WholeSize : _srvBuf[i].SizeBytes };
            }

            for (int i = 0; i < lay.UavCount; i++)
            {
                var b = (VkBuffer)_dev.Lookup(_uav[i].Handle)!;
                binfos[bi++] = new Vk.DescriptorBufferInfo { Buffer = b.Buffer, Offset = _uav[i].Offset, Range = _uav[i].SizeBytes == 0 ? Vk.Vk.WholeSize : _uav[i].SizeBytes };
            }

            for (int i = 0; i < lay.TexSrvCount; i++)
            {
                iinfos[ii++] = new Vk.DescriptorImageInfo { ImageView = _tex[i], ImageLayout = SampledLayoutOf(i) };
            }

            for (int i = 0; i < lay.SamplerCount; i++)
            {
                iinfos[ii++] = new Vk.DescriptorImageInfo { Sampler = _samp[i] };
            }

            Span<Vk.WriteDescriptorSet> writes = stackalloc Vk.WriteDescriptorSet[lay.Total];
            fixed (Vk.DescriptorBufferInfo* pb = binfos)
            fixed (Vk.DescriptorImageInfo* pi = iinfos)
            {
                int wi = 0, bo = 0, io = 0;
                for (int i = 0; i < lay.CbvCount; i++)
                {
                    writes[wi++] = Write(set, (uint)(VkPipelineLayout.CbvBase + i), Vk.DescriptorType.UniformBuffer, pBuf: pb + bo++);
                }

                for (int i = 0; i < lay.SrvCount; i++)
                {
                    writes[wi++] = Write(set, (uint)(lay.SrvBase + i), Vk.DescriptorType.StorageBuffer, pBuf: pb + bo++);
                }

                for (int i = 0; i < lay.UavCount; i++)
                {
                    writes[wi++] = Write(set, (uint)(lay.UavBase + i), Vk.DescriptorType.StorageBuffer, pBuf: pb + bo++);
                }

                for (int i = 0; i < lay.TexSrvCount; i++)
                {
                    writes[wi++] = Write(set, (uint)(lay.TexBase + i), Vk.DescriptorType.SampledImage, pImg: pi + io++);
                }

                for (int i = 0; i < lay.SamplerCount; i++)
                {
                    writes[wi++] = Write(set, (uint)(lay.SamplerBase + i), Vk.DescriptorType.Sampler, pImg: pi + io++);
                }

                fixed (Vk.WriteDescriptorSet* pw = writes)
                {
                    _dev.Api.UpdateDescriptorSets(_dev.Device, (uint)lay.Total, pw, 0, null);
                }
            }

            _dev.DescAlloc.Store(key, set);
        }

        Vk.DescriptorSet bound = set;
        _dev.Api.CmdBindDescriptorSets(Cmd, bindPoint, lay.Layout, 0, 1, &bound, 0, null);
        _descDirty = false;
    }

    // The descriptor's layout must equal the image's actual layout at the draw/dispatch (VUID-VkDescriptorImageInfo-imageLayout-00344):
    // a depth texture bound in DepthReadShaderResource is in DEPTH_STENCIL_READ_ONLY_OPTIMAL, not SHADER_READ_ONLY_OPTIMAL.
    private Vk.ImageLayout SampledLayoutOf(int slot)
    {
        return _texRes[slot]?.SampledLayout ?? Vk.ImageLayout.ShaderReadOnlyOptimal;
    }

    private static Vk.WriteDescriptorSet Write(Vk.DescriptorSet set, uint binding, Vk.DescriptorType type, Vk.DescriptorBufferInfo* pBuf = null, Vk.DescriptorImageInfo* pImg = null)
    {
        return new() { SType = Vk.StructureType.WriteDescriptorSet, DstSet = set, DstBinding = binding, DstArrayElement = 0, DescriptorCount = 1, DescriptorType = type, PBufferInfo = pBuf, PImageInfo = pImg };
    }

    public void BindVertexBuffer(int slot, in GpuBufferView vb, uint stride)
    {
        if (_dev.Lookup(vb.Handle) is VkBuffer b)
        {
            Vk.Buffer bb = b.Buffer;
            ulong off = vb.Offset;
            _dev.Api.CmdBindVertexBuffers(Cmd, (uint)slot, 1, &bb, &off);
        }
    }
    public void BindIndexBuffer(in GpuBufferView ib, bool index32)
    {
        if (_dev.Lookup(ib.Handle) is VkBuffer b)
        {
            _dev.Api.CmdBindIndexBuffer(Cmd, b.Buffer, ib.Offset, index32 ? Vk.IndexType.Uint32 : Vk.IndexType.Uint16);
        }
    }
    public void PushConstants<T>(in T data) where T : unmanaged
    {
        fixed (T* p = &data)
        {
            _dev.Api.CmdPushConstants(Cmd, _dev.BoundLayout, Vk.ShaderStageFlags.All, 0, (uint)sizeof(T), p);
        }
    }
    public void Draw(uint v, uint inst, uint fv, uint fi)
    {
        FlushDescriptors(Vk.PipelineBindPoint.Graphics);
        _dev.Api.CmdDraw(Cmd, v, inst, fv, fi);
    }
    public void DrawIndexed(uint idx, uint inst, uint fidx, int voff, uint fi)
    {
        FlushDescriptors(Vk.PipelineBindPoint.Graphics);
        _dev.Api.CmdDrawIndexed(Cmd, idx, inst, fidx, voff, fi);
    }
    public void DrawIndexedIndirect(in GpuBufferView args, uint offsetBytes, uint drawCount, uint stride)
    {
        FlushDescriptors(Vk.PipelineBindPoint.Graphics);
        if (_dev.Lookup(args.Handle) is VkBuffer b)
        {
            _dev.Api.CmdDrawIndexedIndirect(Cmd, b.Buffer, args.Offset + offsetBytes, drawCount, stride);
        }
    }
    // ---- compute: bind a compute PSO + dispatch, reusing the descriptor flush at the Compute bind point ----
    public void SetComputePipeline(VkComputePipeline p)
    {
        _dev.BoundLayout = p.LayoutObj.Layout;
        _boundLayout = p.LayoutObj;
        _descDirty = true;
        _dev.Api.CmdBindPipeline(Cmd, Vk.PipelineBindPoint.Compute, p.Pipeline);
    }
    public void DispatchCompute(uint gx, uint gy, uint gz)
    {
        FlushDescriptors(Vk.PipelineBindPoint.Compute);
        _dev.Api.CmdDispatch(Cmd, gx, gy, gz);
    }
    public void DispatchComputeIndirect(in GpuBufferView args, uint offsetBytes)
    {
        FlushDescriptors(Vk.PipelineBindPoint.Compute);
        if (_dev.Lookup(args.Handle) is VkBuffer b)
        {
            _dev.Api.CmdDispatchIndirect(Cmd, b.Buffer, args.Offset + offsetBytes);
        }
    }
    public void CopyBuffer(IBuffer src, ulong srcOffsetBytes, IBuffer dst, ulong dstOffsetBytes, ulong sizeBytes)
    {
        if (src is not VkBuffer s || dst is not VkBuffer d)
        {
            return;
        }

        var region = new Vk.BufferCopy { SrcOffset = srcOffsetBytes, DstOffset = dstOffsetBytes, Size = sizeBytes };
        _dev.Api.CmdCopyBuffer(Cmd, s.Buffer, d.Buffer, 1, &region);
    }
    public void CopyBufferToTextureMip(IBuffer src, ulong srcOffsetBytes, ITexture dst, uint mip, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel)
    {
        var s = (VkBuffer)src;
        var d = (VkTexture)dst;
        var copy = new Vk.BufferImageCopy
        {
            BufferOffset = srcOffsetBytes,
            BufferRowLength = bytesPerPixel == 0 ? 0 : srcRowPitchBytes / bytesPerPixel,
            BufferImageHeight = 0,
            ImageSubresource = new Vk.ImageSubresourceLayers(VulkanMap.Aspect(d.Desc.Format), mip, 0, 1),
            ImageOffset = new Vk.Offset3D(0, 0, 0),
            ImageExtent = new Vk.Extent3D(width, height, 1),
        };
        _dev.Api.CmdCopyBufferToImage(Cmd, s.Buffer, d.Image, Vk.ImageLayout.TransferDstOptimal, 1, &copy);
    }

    /// <summary>Record a buffer→image copy (texture upload path). Image must be in TransferDstOptimal.</summary>
    public void CopyBufferToTexture(IBuffer src, ITexture dst, uint width, uint height)
    {
        CopyBufferToTexture((VkBuffer)src, (VkTexture)dst, width, height);
    }

    public void CopyBufferToTexture(VkBuffer src, VkTexture dst, uint width, uint height)
    {
        var copy = new Vk.BufferImageCopy { ImageSubresource = new Vk.ImageSubresourceLayers(VulkanMap.Aspect(dst.Desc.Format), 0, 0, 1), ImageExtent = new Vk.Extent3D(width, height, 1) };
        _dev.Api.CmdCopyBufferToImage(Cmd, src.Buffer, dst.Image, Vk.ImageLayout.TransferDstOptimal, 1, &copy);
    }
    public void CopyBufferToTextureRegion(IBuffer src, ITexture dst, uint dstX, uint dstY, uint width, uint height, uint srcRowPitchBytes, uint bytesPerPixel)
    {
        var s = (VkBuffer)src;
        var d = (VkTexture)dst;
        // bufferRowLength is in TEXELS (== row pitch in bytes / bytes-per-texel); bufferImageHeight=0 means tightly
        // packed in height. imageOffset places the sub-region at the destination (dstX,dstY).
        var copy = new Vk.BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = bytesPerPixel == 0 ? 0 : srcRowPitchBytes / bytesPerPixel,
            BufferImageHeight = 0,
            ImageSubresource = new Vk.ImageSubresourceLayers(VulkanMap.Aspect(d.Desc.Format), 0, 0, 1),
            ImageOffset = new Vk.Offset3D((int)dstX, (int)dstY, 0),
            ImageExtent = new Vk.Extent3D(width, height, 1),
        };
        _dev.Api.CmdCopyBufferToImage(Cmd, s.Buffer, d.Image, Vk.ImageLayout.TransferDstOptimal, 1, &copy);
    }
    /// <summary>Record an image→buffer copy (readback / golden-image path). Image must be in TransferSrcOptimal.</summary>
    public void CopyTextureToBuffer(ITexture src, IBuffer dst, uint width, uint height)
    {
        CopyTextureToBuffer((VkTexture)src, (VkBuffer)dst, width, height);
    }

    public void CopyTextureToBuffer(VkTexture src, VkBuffer dst, uint width, uint height)
    {
        var copy = new Vk.BufferImageCopy { ImageSubresource = new Vk.ImageSubresourceLayers(VulkanMap.Aspect(src.Desc.Format), 0, 0, 1), ImageExtent = new Vk.Extent3D(width, height, 1) };
        _dev.Api.CmdCopyImageToBuffer(Cmd, src.Image, Vk.ImageLayout.TransferSrcOptimal, dst.Buffer, 1, &copy);
    }

    // ---- GPU debug markers: VK_EXT_debug_utils labels (RenderDoc, Nsight, the validation layer). Color ARGB -> RGBA. ----
    public void BeginEvent(string name, uint color)
    {
        if (!Graphics.Diagnostics.Profiler.GpuMarkers)
        {
            return;
        }
#if MEWSTAGE_VK_MARKERS
        ExtDebugUtils? du = _dev.DebugUtils;
        if (du is not null)
        {
            int n = System.Text.Encoding.UTF8.GetByteCount(name);
            Span<byte> buf = stackalloc byte[n + 1];
            System.Text.Encoding.UTF8.GetBytes(name, buf);
            buf[n] = 0;
            Span<float> rgba = stackalloc float[4];
            Graphics.Diagnostics.EventColor.ToRgbaFloat(color, rgba);
            fixed (byte* pn = buf)
            {
                var label = new Vk.DebugUtilsLabelEXT { SType = Vk.StructureType.DebugUtilsLabelExt, PLabelName = pn };
                label.Color[0] = rgba[0];
                label.Color[1] = rgba[1];
                label.Color[2] = rgba[2];
                label.Color[3] = rgba[3];
                du.CmdBeginDebugUtilsLabel(Cmd, &label);
            }
        }
#endif
    }
    public void EndEvent()
    {
        if (!Graphics.Diagnostics.Profiler.GpuMarkers)
        {
            return;
        }
#if MEWSTAGE_VK_MARKERS
        ExtDebugUtils? du = _dev.DebugUtils;
        du?.CmdEndDebugUtilsLabel(Cmd);
#endif
    }
    public void SetMarker(string name, uint color)
    {
        if (!Graphics.Diagnostics.Profiler.GpuMarkers)
        {
            return;
        }
#if MEWSTAGE_VK_MARKERS
        ExtDebugUtils? du = _dev.DebugUtils;
        if (du is null)
        {
            return;
        }

        int n = System.Text.Encoding.UTF8.GetByteCount(name);
        Span<byte> buf = stackalloc byte[n + 1];
        System.Text.Encoding.UTF8.GetBytes(name, buf);
        buf[n] = 0;
        Span<float> rgba = stackalloc float[4];
        Graphics.Diagnostics.EventColor.ToRgbaFloat(color, rgba);
        fixed (byte* pn = buf)
        {
            var label = new Vk.DebugUtilsLabelEXT { SType = Vk.StructureType.DebugUtilsLabelExt, PLabelName = pn };
            label.Color[0] = rgba[0];
            label.Color[1] = rgba[1];
            label.Color[2] = rgba[2];
            label.Color[3] = rgba[3];
            du.CmdInsertDebugUtilsLabel(Cmd, &label);
        }
#endif
    }
    public void Dispose() { }
}

/// <summary>Per-frame VkDescriptorPool + a (layout,resource-tuple)->VkDescriptorSet cache.
/// Reset at frame begin via <see cref="VulkanDevice.ResetFrameDescriptors"/>; in steady state stable bindings hit the cache (alloc-free).</summary>
internal sealed unsafe class VulkanDescriptorAllocator : IDisposable
{
    private readonly VulkanDevice _dev; private Vk.DescriptorPool _pool;
    private readonly Dictionary<ulong, Vk.DescriptorSet> _cache = [];
    public VulkanDescriptorAllocator(VulkanDevice dev, int maxSets)
    {
        _dev = dev;
        Span<Vk.DescriptorPoolSize> sizes =
        [
            new Vk.DescriptorPoolSize { Type = Vk.DescriptorType.UniformBuffer, DescriptorCount = (uint)(maxSets * 4) },
            new Vk.DescriptorPoolSize { Type = Vk.DescriptorType.StorageBuffer, DescriptorCount = (uint)(maxSets * 8) },
            new Vk.DescriptorPoolSize { Type = Vk.DescriptorType.SampledImage, DescriptorCount = (uint)(maxSets * 8) },
            new Vk.DescriptorPoolSize { Type = Vk.DescriptorType.Sampler, DescriptorCount = (uint)(maxSets * 4) },
        ];
        fixed (Vk.DescriptorPoolSize* ps = sizes)
        {
            var ci = new Vk.DescriptorPoolCreateInfo { SType = Vk.StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)maxSets, PoolSizeCount = 4, PPoolSizes = ps };
            VulkanDevice.Check(_dev.Api.CreateDescriptorPool(_dev.Device, &ci, null, out _pool), "CreateDescriptorPool(graphics)");
        }
    }
    public bool TryGet(ulong key, out Vk.DescriptorSet set)
    {
        return _cache.TryGetValue(key, out set);
    }

    public void Store(ulong key, Vk.DescriptorSet set)
    {
        _cache[key] = set;
    }

    public Vk.DescriptorSet Allocate(Vk.DescriptorSetLayout layout)
    {
        Vk.DescriptorSetLayout l = layout;
        var ai = new Vk.DescriptorSetAllocateInfo { SType = Vk.StructureType.DescriptorSetAllocateInfo, DescriptorPool = _pool, DescriptorSetCount = 1, PSetLayouts = &l };
        Vk.DescriptorSet set;
        VulkanDevice.Check(_dev.Api.AllocateDescriptorSets(_dev.Device, &ai, &set), "AllocateDescriptorSets");
        return set;
    }
    public void Reset()
    {
        _ = _dev.Api.ResetDescriptorPool(_dev.Device, _pool, 0);
        _cache.Clear();
    }
    public void Dispose()
    {
        if (_pool.Handle != 0)
        {
            _dev.Api.DestroyDescriptorPool(_dev.Device, _pool, null);
            _pool = default;
        }
    }
}

internal sealed unsafe class VulkanQueue : ICommandQueue, IPresentSyncSubmit
{
    private readonly VulkanDevice _dev;
    private readonly Vk.Queue _queue;
    public QueueKind Kind { get; }
    public VulkanQueue(VulkanDevice dev, Vk.Queue queue, QueueKind kind)
    {
        _dev = dev;
        _queue = queue;
        Kind = kind;
    }

    public ICommandList AcquireCommandList()
    {
        return _dev.AcquireCommandList(Kind);
    }

    /// <summary>A plain submit: NEVER carries the swapchain's acquire/present semaphores (a one-shot upload or barrier
    /// list made between the acquire and the frame's render submit used to take them).</summary>
    public void Submit(ReadOnlySpan<ICommandList> lists, ReadOnlySpan<FenceWait> waitOn, ReadOnlySpan<FenceSignal> signal)
    {
        SubmitCore(lists, waitOn, signal, presenting: false);
    }

    /// <summary>The frame's render submit (<see cref="CommandQueueExtensions.SubmitFrame"/>): attaches the pending
    /// acquire-wait + present-signal the swapchain registered at acquire.</summary>
    public void SubmitPresenting(ReadOnlySpan<ICommandList> lists, ReadOnlySpan<FenceWait> waitOn, ReadOnlySpan<FenceSignal> signal)
    {
        SubmitCore(lists, waitOn, signal, presenting: true);
    }

    private void SubmitCore(ReadOnlySpan<ICommandList> lists, ReadOnlySpan<FenceWait> waitOn, ReadOnlySpan<FenceSignal> signal, bool presenting)
    {
        // Inject the swapchain's binary acquire-wait + present-signal onto the frame's render submit ONLY (explicit:
        // SubmitPresenting). Timeline fences (waitOn/signal) cannot express swapchain binary semaphores, so without
        // this the acquire->render->present chain has NO GPU sync vs the presentation engine.
        Vk.Semaphore acqSem = default, presSem = default;
        bool present = presenting && Kind == QueueKind.Graphics && _dev.TryTakePresentSync(out acqSem, out presSem);
        Span<Vk.CommandBufferSubmitInfo> cbs = stackalloc Vk.CommandBufferSubmitInfo[lists.Length == 0 ? 1 : lists.Length];
        for (int i = 0; i < lists.Length; i++)
        {
            cbs[i] = new Vk.CommandBufferSubmitInfo { SType = Vk.StructureType.CommandBufferSubmitInfo, CommandBuffer = ((VulkanCommandList)lists[i]).Cmd };
        }

        int waitN = waitOn.Length + (present ? 1 : 0);
        int sigN = signal.Length + (present ? 1 : 0);
        Span<Vk.SemaphoreSubmitInfo> waits = stackalloc Vk.SemaphoreSubmitInfo[waitN == 0 ? 1 : waitN];
        for (int i = 0; i < waitOn.Length; i++)
        {
            waits[i] = new Vk.SemaphoreSubmitInfo { SType = Vk.StructureType.SemaphoreSubmitInfo, Semaphore = ((VkFence)waitOn[i].Fence).Timeline, Value = waitOn[i].Value, StageMask = Vk.PipelineStageFlags2.AllCommandsBit };
        }

        Span<Vk.SemaphoreSubmitInfo> sigs = stackalloc Vk.SemaphoreSubmitInfo[sigN == 0 ? 1 : sigN];
        for (int i = 0; i < signal.Length; i++)
        {
            sigs[i] = new Vk.SemaphoreSubmitInfo { SType = Vk.StructureType.SemaphoreSubmitInfo, Semaphore = ((VkFence)signal[i].Fence).Timeline, Value = signal[i].Value, StageMask = Vk.PipelineStageFlags2.AllCommandsBit };
        }

        if (present)
        {
            // Binary semaphores: Value is ignored. Wait acquire at color-output (first write to the swapchain
            // image); signal present at all-commands so the presentation engine waits for the whole frame's work.
            waits[waitOn.Length] = new Vk.SemaphoreSubmitInfo { SType = Vk.StructureType.SemaphoreSubmitInfo, Semaphore = acqSem, Value = 0, StageMask = Vk.PipelineStageFlags2.ColorAttachmentOutputBit };
            sigs[signal.Length] = new Vk.SemaphoreSubmitInfo { SType = Vk.StructureType.SemaphoreSubmitInfo, Semaphore = presSem, Value = 0, StageMask = Vk.PipelineStageFlags2.AllCommandsBit };
        }

        fixed (Vk.CommandBufferSubmitInfo* pcb = cbs)
        fixed (Vk.SemaphoreSubmitInfo* pw = waits)
        fixed (Vk.SemaphoreSubmitInfo* ps = sigs)
        {
            var si = new Vk.SubmitInfo2
            {
                SType = Vk.StructureType.SubmitInfo2,
                CommandBufferInfoCount = (uint)lists.Length,
                PCommandBufferInfos = lists.Length == 0 ? null : pcb,
                WaitSemaphoreInfoCount = (uint)waitN,
                PWaitSemaphoreInfos = waitN == 0 ? null : pw,
                SignalSemaphoreInfoCount = (uint)sigN,
                PSignalSemaphoreInfos = sigN == 0 ? null : ps,
            };
            VulkanDevice.Check(_dev.Api.QueueSubmit2(_queue, 1, &si, default), "QueueSubmit2");
        }

        if (_dev.Lifetime.Enabled)
        {
            long serial = _dev.Lifetime.OnSubmit();
            for (int i = 0; i < signal.Length; i++)
            {
                ((VkFence)signal[i].Fence).NoteSignal(signal[i].Value, serial);
            }
        }
    }

    public ulong Signal(IFence fence)
    {
        var f = (VkFence)fence;
        ulong next = f.CompletedValue + 1;
        var sig = new FenceSignal[1];
        sig[0] = new FenceSignal(fence, next);
        Submit(default, default, sig);
        return next;
    }
    public void WaitIdle()
    {
        if (!_dev.DeviceAlive)
        {
            return;
        }

        try
        {
            _ = _dev.Api.QueueWaitIdle(_queue);
            _dev.Lifetime.OnIdle();
        }
        catch { /* queue/device already torn down */ }
    }

    // ---- GPU debug markers on the queue timeline (VK_EXT_debug_utils queue labels;). ----
    public void BeginEvent(string name, uint color)
    {
#if MEWSTAGE_VK_MARKERS
        ExtDebugUtils? du = _dev.DebugUtils;
        if (du is null || !Graphics.Diagnostics.Profiler.GpuMarkers)
        {
            return;
        }

        int n = System.Text.Encoding.UTF8.GetByteCount(name);
        Span<byte> buf = stackalloc byte[n + 1];
        System.Text.Encoding.UTF8.GetBytes(name, buf);
        buf[n] = 0;
        Span<float> rgba = stackalloc float[4];
        Graphics.Diagnostics.EventColor.ToRgbaFloat(color, rgba);
        fixed (byte* pn = buf)
        {
            var label = new Vk.DebugUtilsLabelEXT { SType = Vk.StructureType.DebugUtilsLabelExt, PLabelName = pn };
            label.Color[0] = rgba[0];
            label.Color[1] = rgba[1];
            label.Color[2] = rgba[2];
            label.Color[3] = rgba[3];
            du.QueueBeginDebugUtilsLabel(_queue, &label);
        }
#endif
    }
    public void EndEvent()
    {
#if MEWSTAGE_VK_MARKERS
        ExtDebugUtils? du = _dev.DebugUtils;
        if (du is not null && Graphics.Diagnostics.Profiler.GpuMarkers)
        {
            du.QueueEndDebugUtilsLabel(_queue);
        }
#endif
    }
    public void SetMarker(string name, uint color)
    {
#if MEWSTAGE_VK_MARKERS
        ExtDebugUtils? du = _dev.DebugUtils;
        if (du is null || !Graphics.Diagnostics.Profiler.GpuMarkers)
        {
            return;
        }

        int n = System.Text.Encoding.UTF8.GetByteCount(name);
        Span<byte> buf = stackalloc byte[n + 1];
        System.Text.Encoding.UTF8.GetBytes(name, buf);
        buf[n] = 0;
        Span<float> rgba = stackalloc float[4];
        Graphics.Diagnostics.EventColor.ToRgbaFloat(color, rgba);
        fixed (byte* pn = buf)
        {
            var label = new Vk.DebugUtilsLabelEXT { SType = Vk.StructureType.DebugUtilsLabelExt, PLabelName = pn };
            label.Color[0] = rgba[0];
            label.Color[1] = rgba[1];
            label.Color[2] = rgba[2];
            label.Color[3] = rgba[3];
            du.QueueInsertDebugUtilsLabel(_queue, &label);
        }
#endif
    }
}

/// <summary>The Vulkan 1.3 device. Headless-capable (no surface needed for offscreen/CI).</summary>
public sealed unsafe class VulkanDevice : IGraphicsDevice
{
    private Vk.Instance _instance;

    // Optional Vulkan validation (env MEWSTAGE_VK_VALIDATION=1): VK_LAYER_KHRONOS_validation + VK_EXT_debug_utils, with
    // a debug messenger that prints WARNING/ERROR (incl. VUID + device-lost) to stderr. Needs the layer installed
    // (Vulkan SDK / runtime); degrades gracefully to no validation if absent.
    private Vk.DebugUtilsMessengerEXT _debugMessenger;
    private static readonly string[] ValidationLayers = ["VK_LAYER_KHRONOS_validation"];
    private static readonly Vk.PfnDebugUtilsMessengerCallbackEXT DebugCallbackPointer = new(DebugCallback);
    private static uint DebugCallback(Vk.DebugUtilsMessageSeverityFlagsEXT severity,
        Vk.DebugUtilsMessageTypeFlagsEXT types, Vk.DebugUtilsMessengerCallbackDataEXT* data, void* user)
    {
        try
        {
            string msg = (data != null && data->PMessage != null) ? (SilkMarshal.PtrToString((nint)data->PMessage) ?? "") : "";
            Console.Error.WriteLine($"[VK-VALIDATION {severity}] {msg}");
            if ((severity & Vk.DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt) != 0)
            {
                GpuDeviceDiagnostics.ValidationError(msg);
            }

            Console.Error.Flush();
        }
        catch { /* never let the callback throw into the driver */ }

        return 0; // VK_FALSE
    }
    private Vk.PhysicalDevice _phys;
    private Vk.Device _device;
    private Vk.Queue _queue;
    private Vk.CommandPool _cmdPool;
    private Vk.PipelineCache _psoCache;
    private Vk.PhysicalDeviceMemoryProperties _memProps;
    private readonly Dictionary<ulong, VkResource> _registry = [];
    private readonly DeferredDestroyQueue _deferred = new();   // the shared fence-keyed ring
    private long _retireFrame;
    /// <summary>Upper bound of the scheduler depth the lifetime validator's frame ring is sized for (GpuScheduler
    /// runs 2-3 deep; the DX12 device's shader-visible heap partition uses the same bound). Deeper grows it once.</summary>
    private const int LifetimeFrameRing = 4;
    /// <summary>The debug-only lifetime check (MEWSTAGE_GPU_VALIDATE=1 or MEWSTAGE_VK_VALIDATION=1).</summary>
    internal readonly GpuLifetimeValidator Lifetime = new(GpuLifetimeValidator.IsRequested("MEWSTAGE_VK_VALIDATION"), LifetimeFrameRing);
    /// <summary>Lifetime violations detected (0 unless validation is on).</summary>
    public int LifetimeViolations => Lifetime.Violations;
    /// <summary>True when VK_LAYER_KHRONOS_validation and its messenger are active (tests report it).</summary>
    public bool ValidationLayerEnabled { get; private set; }
    /// <summary>True when the lifetime check runs on this device.</summary>
    public bool LifetimeValidationEnabled => Lifetime.Enabled;
    private readonly List<VulkanCommandList> _cmdLists = [];
    private long _handleCounter;
    private readonly Lock _gate = new();

    /// <summary>VkPhysicalDeviceDriverProperties.driverName (lavapipe: "llvmpipe").</summary>
    public string DriverName { get; private set; } = "";
    /// <summary>VkPhysicalDeviceDriverProperties.driverInfo (lavapipe: "Mesa 25.2.8-0ubuntu0.24.04.4 (LLVM 20.1.2)").</summary>
    public string DriverInfo { get; private set; } = "";
    /// <summary>VkPhysicalDeviceProperties.deviceName.</summary>
    public string DeviceName { get; private set; } = "";
    /// <summary>VkPhysicalDeviceProperties.apiVersion as "major.minor.patch".</summary>
    public string ApiVersion { get; private set; } = "";
    // VK_API_VERSION_MAJOR/MINOR/PATCH (Vulkan spec 3.9.2.1 "Version Numbers"): variant 31..29, major 28..22, minor 21..12, patch 11..0.
    private const int ApiMajorShift = 22, ApiMinorShift = 12;
    private const uint ApiMajorMask = 0x7Fu, ApiMinorMask = 0x3FFu, ApiPatchMask = 0xFFFu;

    internal Vk.Vk Api { get; }
    internal Vk.Device Device => _device;
    internal Vk.Instance Instance => _instance;
    /// <summary>Raw VkInstance handle (for the host to create a VkSurfaceKHR via SDL_Vulkan_CreateSurface).</summary>
    public ulong InstanceHandle => (ulong)_instance.Handle;
    internal Vk.PhysicalDevice PhysicalDevice => _phys;
    internal Vk.Queue GraphicsQueueRaw => _queue;
    /// <summary>Nanoseconds per timestamp-query tick (VkPhysicalDeviceLimits.timestampPeriod).</summary>
    internal float TimestampPeriodNs { get; private set; } = 1f;

    /// <summary>The VK_EXT_debug_utils extension object (non-null when validation OR -p:EnableVkMarkers is active),
    /// used by the command-list/queue marker seam + <see cref="SetDebugName"/>.</summary>
    internal ExtDebugUtils? DebugUtils { get; private set; }
    internal bool DeviceAlive => _device.Handle != 0;
    internal uint QueueFamilyIndex { get; private set; }
    // --- Swapchain presentation sync (binary semaphores) ---
    // The scheduler submits the frame's render command list with only timeline-fence sync (the BeginFrame throttle).
    // Ordering the acquire->render->present chain against the presentation engine needs BINARY semaphores, which a
    // timeline fence cannot express. The swapchain registers the current frame's acquire-wait + present-signal
    // binary semaphores here on AcquireNextBackbuffer; the NEXT graphics submit that carries command lists consumes
    // them (waits acquire at COLOR_ATTACHMENT_OUTPUT, signals present), then clears. Present() waits present.
    private Vk.Semaphore _pendingAcquireSem;
    private Vk.Semaphore _pendingPresentSem;
    private bool _hasPresentSync;
    internal void RegisterPresentSync(Vk.Semaphore acquireWait, Vk.Semaphore presentSignal)
    {
        _pendingAcquireSem = acquireWait;
        _pendingPresentSem = presentSignal;
        _hasPresentSync = true;
    }
    internal bool TryTakePresentSync(out Vk.Semaphore acquireWait, out Vk.Semaphore presentSignal)
    {
        acquireWait = _pendingAcquireSem;
        presentSignal = _pendingPresentSem;
        bool h = _hasPresentSync;
        _hasPresentSync = false;
        return h;
    }

    // Safety net, called by VulkanSwapchain.Present: if nothing claimed this frame's sync (a presenting path that did not
    // use SubmitFrame), attach it to an empty submit so present never waits a semaphore nobody signals. The image's
    // rendering is then NOT ordered against the acquire, so say so once - the presenting path must be fixed.
    private bool _unclaimedPresentWarned;
    internal void SubmitUnclaimedPresentSync()
    {
        if (!_hasPresentSync)
        {
            return;
        }

        if (!_unclaimedPresentWarned)
        {
            _unclaimedPresentWarned = true;
            Console.Error.WriteLine("[vk] a frame was presented without SubmitFrame: its acquire/present sync went on an empty submit (fix the presenting path)");
        }

        ((IPresentSyncSubmit)GraphicsQueue).SubmitPresenting(default, default, default);
    }
    private readonly bool _presentation;
    internal Vk.PipelineLayout BoundLayout { get; set; }
    // Descriptor allocators are N-buffered by frames-in-flight (GBV use-after-free fix): a SINGLE pool reset every
    // BeginFrame freed the VkDescriptorSets (and cleared the bind cache) that frame N's still-in-flight command
    // buffer referenced — GpuScheduler runs FramesInFlight deep, so BeginFrame(N+1) only waited frame N-1's fence.
    // One pool + cache per in-flight frame; only the slot just (re)entered (its fence waited) is reset.
    private VulkanDescriptorAllocator?[] _descAllocs = new VulkanDescriptorAllocator?[1];
    private int _descSlot;                 // current in-flight frame slot (set by ResetFrameDescriptors)
    private readonly int _descPerPool = 4096;       // sets per pool (per frame)
    /// <summary>The CURRENT in-flight frame's descriptor allocator (per-slot pool + bind cache). Lazily created.</summary>
    internal VulkanDescriptorAllocator DescAlloc => _descAllocs[_descSlot] ??= new VulkanDescriptorAllocator(this, _descPerPool);
    /// <summary>Rewind the CURRENT in-flight frame's descriptor pool + set cache at frame begin (
    /// step 2). <paramref name="frameSlot"/> = frameNo % FramesInFlight; the slot's frame fence was waited in
    /// BeginFrame, so resetting only its pool can never free a set an earlier still-in-flight frame references.</summary>
    public void ResetFrameDescriptors(int frameSlot, int framesInFlight)
    {
        int fif = framesInFlight < 1 ? 1 : framesInFlight;
        if (_descAllocs.Length < fif)
        {
            var grown = new VulkanDescriptorAllocator?[fif];
            Array.Copy(_descAllocs, grown, _descAllocs.Length);
            _descAllocs = grown;
        }

        _descSlot = ((frameSlot % fif) + fif) % fif;
        _descAllocs[_descSlot]?.Reset();
    }

    /// <inheritdoc />
    public GpuCapabilities Caps { get; private set; }
    /// <summary>The physical device supports VkPolygonMode LINE (enabled at creation); wireframe debug pipelines fall back to solid without it.</summary>
    public bool FillModeNonSolid { get; private set; }
    /// <inheritdoc />
    public bool IsUma => Caps.IsUma;
    /// <inheritdoc />
    public ICommandQueue GraphicsQueue { get; private set; } = null!;
    /// <inheritdoc />
    public ICommandQueue ComputeQueue { get; private set; } = null!;
    /// <inheritdoc />
    public ICommandQueue CopyQueue { get; private set; } = null!;

    /// <summary>Creates the device on the first suitable physical device (a discrete GPU first, then anything that
    /// supports Vulkan 1.3; Mesa lavapipe qualifies).</summary>
    public VulkanDevice()
    {
        Api = Vk.Vk.GetApi();
        Init();
    }

    /// <summary>Presentation-enabled device: instance carries VK_KHR_surface (+ platform surface), device carries VK_KHR_swapchain. Headless path (default ctor) is unchanged.</summary>
    public VulkanDevice(bool enablePresentation)
    {
        _presentation = enablePresentation;
        Api = Vk.Vk.GetApi();
        Init();
    }

    private bool HasInstanceLayer(string layer)
    {
        uint lc = 0;
        _ = Api.EnumerateInstanceLayerProperties(ref lc, null);
        if (lc == 0)
        {
            return false;
        }

        Vk.LayerProperties* lp = stackalloc Vk.LayerProperties[(int)lc];
        _ = Api.EnumerateInstanceLayerProperties(ref lc, lp);
        for (uint i = 0; i < lc; i++)
        {
            if (SilkMarshal.PtrToString((nint)lp[i].LayerName) == layer)
            {
                return true;
            }
        }

        return false;
    }

    private void Init()
    {
        byte* name = (byte*)SilkMarshal.StringToPtr("MewStage");
        var ai = new Vk.ApplicationInfo { SType = Vk.StructureType.ApplicationInfo, PApplicationName = name, PEngineName = name, ApiVersion = Vk.Vk.Version13 };
        var ici = new Vk.InstanceCreateInfo { SType = Vk.StructureType.InstanceCreateInfo, PApplicationInfo = &ai };

        var exts = new List<string>();
        if (_presentation)
        {
            string plat = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "VK_KHR_win32_surface"
                        : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "VK_EXT_metal_surface" : "VK_KHR_xlib_surface";
            exts.Add("VK_KHR_surface");
            exts.Add(plat);
        }

        bool wantValidation = Environment.GetEnvironmentVariable("MEWSTAGE_VK_VALIDATION") == "1";
        bool haveValidation = wantValidation && HasInstanceLayer("VK_LAYER_KHRONOS_validation");
        if (wantValidation && !haveValidation)
        {
            Console.Error.WriteLine("[vk] MEWSTAGE_VK_VALIDATION=1 but VK_LAYER_KHRONOS_validation not found (install the Vulkan SDK/runtime). Continuing without validation.");
        }
        // MEWSTAGE_VK_MARKERS=1 requests VK_EXT_debug_utils so the marker seam emits GPU labels (RenderDoc, Nsight).
        bool wantMarkers = Environment.GetEnvironmentVariable("MEWSTAGE_VK_MARKERS") == "1";
        if (haveValidation || wantMarkers)
        {
            exts.Add("VK_EXT_debug_utils");
        }

        nint instExt = exts.Count > 0 ? SilkMarshal.StringArrayToPtr([.. exts]) : 0;
        if (instExt != 0)
        {
            ici.EnabledExtensionCount = (uint)exts.Count;
            ici.PpEnabledExtensionNames = (byte**)instExt;
        }

        nint layerPtr = 0;
        if (haveValidation)
        {
            layerPtr = SilkMarshal.StringArrayToPtr(ValidationLayers);
            ici.EnabledLayerCount = 1;
            ici.PpEnabledLayerNames = (byte**)layerPtr;
        }

        Check(Api.CreateInstance(&ici, null, out _instance), "CreateInstance");
        if (instExt != 0)
        {
            _ = SilkMarshal.Free(instExt);
        }

        if (layerPtr != 0)
        {
            _ = SilkMarshal.Free(layerPtr);
        }

        _ = SilkMarshal.Free((nint)name);

        if ((haveValidation || wantMarkers) && Api.TryGetInstanceExtension(_instance, out ExtDebugUtils du))
        {
            DebugUtils = du;   // used by BOTH the optional validation messenger AND the GPU debug-label marker seam
            if (haveValidation)
            {
                var mci = new Vk.DebugUtilsMessengerCreateInfoEXT
                {
                    SType = Vk.StructureType.DebugUtilsMessengerCreateInfoExt,
                    MessageSeverity = Vk.DebugUtilsMessageSeverityFlagsEXT.WarningBitExt | Vk.DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
                    MessageType = Vk.DebugUtilsMessageTypeFlagsEXT.GeneralBitExt | Vk.DebugUtilsMessageTypeFlagsEXT.ValidationBitExt | Vk.DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
                    PfnUserCallback = DebugCallbackPointer,
                };
                if (du.CreateDebugUtilsMessenger(_instance, &mci, null, out _debugMessenger) == Vk.Result.Success)
                {
                    ValidationLayerEnabled = true;
                    Console.Error.WriteLine("[vk] validation layer + debug messenger ENABLED (MEWSTAGE_VK_VALIDATION=1).");
                }
            }
        }

        uint n = 0;
        _ = Api.EnumeratePhysicalDevices(_instance, ref n, null);
        if (n == 0)
        {
            throw new InvalidOperationException("no Vulkan physical devices");
        }

        Vk.PhysicalDevice* pds = stackalloc Vk.PhysicalDevice[(int)n];
        _ = Api.EnumeratePhysicalDevices(_instance, ref n, pds);
        _phys = pds[0];

        uint qn = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(_phys, ref qn, null);
        Vk.QueueFamilyProperties* qfs = stackalloc Vk.QueueFamilyProperties[(int)qn];
        Api.GetPhysicalDeviceQueueFamilyProperties(_phys, ref qn, qfs);
        QueueFamilyIndex = uint.MaxValue;
        for (uint i = 0; i < qn; i++)
        {
            if ((qfs[i].QueueFlags & Vk.QueueFlags.GraphicsBit) != 0)
            {
                QueueFamilyIndex = i;
                break;
            }
        }

        if (QueueFamilyIndex == uint.MaxValue)
        {
            throw new InvalidOperationException("no graphics queue family");
        }

        float prio = 1f;
        var qci = new Vk.DeviceQueueCreateInfo { SType = Vk.StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = QueueFamilyIndex, QueueCount = 1, PQueuePriorities = &prio };
        var f13 = new Vk.PhysicalDeviceVulkan13Features { SType = Vk.StructureType.PhysicalDeviceVulkan13Features, DynamicRendering = true, Synchronization2 = true };
        var f12 = new Vk.PhysicalDeviceVulkan12Features { SType = Vk.StructureType.PhysicalDeviceVulkan12Features, TimelineSemaphore = true, HostQueryReset = true, ScalarBlockLayout = true, PNext = &f13 };
        // core features: non-solid fill for the wireframe debug pipelines when the device has it
        Api.GetPhysicalDeviceFeatures(_phys, out Vk.PhysicalDeviceFeatures physFeatures);
        FillModeNonSolid = physFeatures.FillModeNonSolid;
        var f10 = new Vk.PhysicalDeviceFeatures { FillModeNonSolid = FillModeNonSolid };
        var dci = new Vk.DeviceCreateInfo { SType = Vk.StructureType.DeviceCreateInfo, PNext = &f12, QueueCreateInfoCount = 1, PQueueCreateInfos = &qci, PEnabledFeatures = &f10 };
        nint devExt = 0;
        if (_presentation)
        {
            string[] names = ["VK_KHR_swapchain"];
            devExt = SilkMarshal.StringArrayToPtr(names);
            dci.EnabledExtensionCount = (uint)names.Length;
            dci.PpEnabledExtensionNames = (byte**)devExt;
        }

        Check(Api.CreateDevice(_phys, &dci, null, out _device), "CreateDevice");
        if (devExt != 0)
        {
            _ = SilkMarshal.Free(devExt);
        }

        Api.GetDeviceQueue(_device, QueueFamilyIndex, 0, out _queue);

        var pci = new Vk.CommandPoolCreateInfo { SType = Vk.StructureType.CommandPoolCreateInfo, QueueFamilyIndex = QueueFamilyIndex, Flags = Vk.CommandPoolCreateFlags.ResetCommandBufferBit };
        Check(Api.CreateCommandPool(_device, &pci, null, out _cmdPool), "CreateCommandPool");
        InitPsoCache();

        Api.GetPhysicalDeviceMemoryProperties(_phys, out _memProps);
        // UMA if every memory type that's device-local is also host-visible (integrated / llvmpipe).
        bool uma = true;
        for (uint i = 0; i < _memProps.MemoryTypeCount; i++)
        {
            Vk.MemoryPropertyFlags p = _memProps.MemoryTypes[(int)i].PropertyFlags;
            if ((p & Vk.MemoryPropertyFlags.DeviceLocalBit) != 0 && (p & Vk.MemoryPropertyFlags.HostVisibleBit) == 0)
            {
                uma = false;
                break;
            }
        }

        Api.GetPhysicalDeviceProperties(_phys, out Vk.PhysicalDeviceProperties props);
        // VkPhysicalDeviceDriverProperties (core 1.2) names the driver build the golden tests were
        // recorded with (lavapipe: driverName "llvmpipe", driverInfo "Mesa <version> (LLVM <version>)").
        var drv = new Vk.PhysicalDeviceDriverProperties { SType = Vk.StructureType.PhysicalDeviceDriverProperties };
        var p2 = new Vk.PhysicalDeviceProperties2 { SType = Vk.StructureType.PhysicalDeviceProperties2, PNext = &drv };
        Api.GetPhysicalDeviceProperties2(_phys, &p2);
        DriverName = SilkMarshal.PtrToString((nint)drv.DriverName) ?? "";
        DriverInfo = SilkMarshal.PtrToString((nint)drv.DriverInfo) ?? "";
        DeviceName = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "";
        ApiVersion = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{(props.ApiVersion >> ApiMajorShift) & ApiMajorMask}.{(props.ApiVersion >> ApiMinorShift) & ApiMinorMask}.{props.ApiVersion & ApiPatchMask}");
        TimestampPeriodNs = props.Limits.TimestampPeriod;   // ns per timestamp tick
        int msaa = 4;
        Vk.SampleCountFlags counts = props.Limits.FramebufferColorSampleCounts & props.Limits.FramebufferDepthSampleCounts;
        if ((counts & Vk.SampleCountFlags.Count8Bit) != 0)
        {
            msaa = 8;
        }
        else
        {
            msaa = (counts & Vk.SampleCountFlags.Count4Bit) != 0 ? 4 : 1;
        }
        // Bindless tier detection: runtimeDescriptorArray + partially-bound (off by default;
        // the baseline uses per-draw descriptor tables). Detection only — not enabled unless a bindless path opts in.
        var idx = new Vk.PhysicalDeviceDescriptorIndexingFeatures { SType = Vk.StructureType.PhysicalDeviceDescriptorIndexingFeatures };
        var f2 = new Vk.PhysicalDeviceFeatures2 { SType = Vk.StructureType.PhysicalDeviceFeatures2, PNext = &idx };
        Api.GetPhysicalDeviceFeatures2(_phys, &f2);
        BindlessTier bindless = (idx.RuntimeDescriptorArray && idx.DescriptorBindingPartiallyBound) ? BindlessTier.Tier1 : BindlessTier.None;
        IndirectTier indirect = IndirectTier.MultiDraw;   // vkCmdDrawIndexedIndirect is core; drawCount=1 needs no feature
        Caps = GpuCapabilities.Baseline with { IsUma = uma, MaxMsaaSamples = msaa, Bindless = bindless, IndirectDraw = indirect };

        GraphicsQueue = new VulkanQueue(this, _queue, QueueKind.Graphics);
        ComputeQueue = GraphicsQueue;   // single-queue baseline
        CopyQueue = new VulkanQueue(this, _queue, QueueKind.Copy);
    }

    internal ulong NextHandle()
    {
        return (ulong)Interlocked.Increment(ref _handleCounter);
    }

    internal void Register(VkResource r)
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
    internal VkResource? Lookup(ulong h)
    {
        lock (_gate)
        {
            if (!_registry.TryGetValue(h, out VkResource? r))
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

    /// <summary>MEWSTAGE_GPU_POISON=1 at construction - every buffer and image this device creates is filled with
    /// <see cref="GpuPoison.Byte"/> (MEWSTAGE_GPU_POISON=nan: GpuPoison.NanByte) before first use (host-visible memory: mapped and filled at
    /// allocation; device-only memory: a one-shot vkCmdFillBuffer / vkCmdClear*Image). Debug / test only.</summary>
    public bool PoisonNewAllocations => PoisonFill is not null;
    /// <summary>The fill byte of <see cref="PoisonNewAllocations"/> (GpuPoison.Byte, or GpuPoison.NanByte for MEWSTAGE_GPU_POISON=nan), or null.</summary>
    public byte? PoisonFill { get; } = GpuPoison.Requested();

    /// <summary>Allocates (see the other overload) and, with <see cref="PoisonNewAllocations"/>, poisons host-visible memory
    /// in place; <paramref name="gpuPoison"/> is true when the memory is not host visible and the caller must poison the
    /// bound resource with <see cref="PoisonOnGpu"/>.</summary>
    internal Vk.DeviceMemory AllocateMemory(Vk.MemoryRequirements req, Vk.MemoryPropertyFlags props, out bool gpuPoison)
    {
        Vk.DeviceMemory mem = AllocateMemory(req, props, out uint type);
        gpuPoison = false;
        if (!PoisonNewAllocations)
        {
            return mem;
        }

        if ((_memProps.MemoryTypes[(int)type].PropertyFlags & Vk.MemoryPropertyFlags.HostVisibleBit) == 0)
        {
            gpuPoison = true;
            return mem;
        }

        void* p;
        Check(Api.MapMemory(_device, mem, 0, Vk.Vk.WholeSize, 0, &p), "MapMemory(poison)");
        GpuPoison.Fill(p, req.Size, PoisonFill!.Value);
        if ((_memProps.MemoryTypes[(int)type].PropertyFlags & Vk.MemoryPropertyFlags.HostCoherentBit) == 0)
        {
            var range = new Vk.MappedMemoryRange { SType = Vk.StructureType.MappedMemoryRange, Memory = mem, Offset = 0, Size = Vk.Vk.WholeSize };
            _ = Api.FlushMappedMemoryRanges(_device, 1, &range);
        }

        Api.UnmapMemory(_device, mem);
        return mem;
    }

    private readonly Lock _poisonGate = new();

    /// <summary>Poisons a buffer (vkCmdFillBuffer) or an image (vkCmdClearColorImage / vkCmdClearDepthStencilImage)
    /// whose memory the host cannot map, with a one-shot submit and a wait (allocation time only). Color: UNORM formats get
    /// the poison byte exactly (Byte / 255), float formats the poison word read as a float (-4.3e8), integer formats the
    /// word; depth gets Byte / 255 (a depth clear must lie in [0, 1]). The image is left in TRANSFER_DST_OPTIMAL; the render
    /// graph's first barrier names UNDEFINED (ResourceState.Common), which a driver may treat as a discard - lavapipe and
    /// every UMA device take the host-visible path above instead. Compressed formats are skipped (no clear exists for
    /// them; they are always uploaded whole).</summary>
    internal void PoisonOnGpu(Vk.Buffer buffer, Vk.Image image, in TextureDesc d)
    {
        if (image.Handle != 0 && d.Format is Format.BC5_UNorm or Format.BC7_UNorm or Format.BC7_UNorm_sRGB or Format.ASTC_4x4_UNorm or Format.ASTC_4x4_UNorm_sRGB)
        {
            return;
        }

        byte fill = PoisonFill!.Value;
        lock (_poisonGate)
        {
            var ai = new Vk.CommandBufferAllocateInfo { SType = Vk.StructureType.CommandBufferAllocateInfo, CommandPool = _cmdPool, Level = Vk.CommandBufferLevel.Primary, CommandBufferCount = 1 };
            Vk.CommandBuffer cb;
            Check(Api.AllocateCommandBuffers(_device, &ai, &cb), "AllocateCommandBuffers(poison)");
            var bi = new Vk.CommandBufferBeginInfo { SType = Vk.StructureType.CommandBufferBeginInfo, Flags = Vk.CommandBufferUsageFlags.OneTimeSubmitBit };
            _ = Api.BeginCommandBuffer(cb, &bi);
            if (buffer.Handle != 0)
            {
                Api.CmdFillBuffer(cb, buffer, 0, Vk.Vk.WholeSize, GpuPoison.Word(fill));
            }
            else
            {
                Vk.ImageAspectFlags aspect = VulkanMap.Aspect(d.Format);
                var range = new Vk.ImageSubresourceRange(aspect, 0, Vk.Vk.RemainingMipLevels, 0, Vk.Vk.RemainingArrayLayers);
                var barrier = new Vk.ImageMemoryBarrier
                {
                    SType = Vk.StructureType.ImageMemoryBarrier,
                    SrcAccessMask = 0,
                    DstAccessMask = Vk.AccessFlags.TransferWriteBit,
                    OldLayout = Vk.ImageLayout.Undefined,
                    NewLayout = Vk.ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.Vk.QueueFamilyIgnored,
                    Image = image,
                    SubresourceRange = range,
                };
                Api.CmdPipelineBarrier(cb, Vk.PipelineStageFlags.TopOfPipeBit, Vk.PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, &barrier);
                float unorm = fill / (float)byte.MaxValue;
                if (VulkanMap.IsDepth(d.Format))
                {
                    var dv = new Vk.ClearDepthStencilValue(unorm, fill);
                    Api.CmdClearDepthStencilImage(cb, image, Vk.ImageLayout.TransferDstOptimal, &dv, 1, &range);
                }
                else
                {
                    Vk.ClearColorValue cv;
                    if (d.Format is Format.R8_UInt or Format.R16_UInt or Format.R32_UInt)
                    {
                        uint w = GpuPoison.Word(fill);
                        cv = new Vk.ClearColorValue(uint32_0: w, uint32_1: w, uint32_2: w, uint32_3: w);
                    }
                    else if (d.Format is Format.R16_Float or Format.R16G16_Float or Format.R16G16B16A16_Float or Format.R32_Float or Format.R32G32_Float
                             or Format.R32G32B32_Float or Format.R32G32B32A32_Float or Format.R11G11B10_Float)
                    {
                        float f = GpuPoison.AsFloat(fill);
                        cv = new Vk.ClearColorValue(f, f, f, f);
                    }
                    else
                    {
                        cv = new Vk.ClearColorValue(unorm, unorm, unorm, unorm);
                    }

                    Api.CmdClearColorImage(cb, image, Vk.ImageLayout.TransferDstOptimal, &cv, 1, &range);
                }
            }

            _ = Api.EndCommandBuffer(cb);
            var fci = new Vk.FenceCreateInfo { SType = Vk.StructureType.FenceCreateInfo };
            Check(Api.CreateFence(_device, &fci, null, out Vk.Fence fence), "CreateFence(poison)");
            var si = new Vk.SubmitInfo { SType = Vk.StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cb };
            Check(Api.QueueSubmit(_queue, 1, &si, fence), "QueueSubmit(poison)");
            Check(Api.WaitForFences(_device, 1, &fence, true, ulong.MaxValue), "WaitForFences(poison)");
            Api.DestroyFence(_device, fence, null);
            Api.FreeCommandBuffers(_device, _cmdPool, 1, &cb);
        }

        GpuPoison.CountFill();
    }

    internal Vk.DeviceMemory AllocateMemory(Vk.MemoryRequirements req, Vk.MemoryPropertyFlags props)
    {
        return AllocateMemory(req, props, out uint _);
    }

    private Vk.DeviceMemory AllocateMemory(Vk.MemoryRequirements req, Vk.MemoryPropertyFlags props, out uint type)
    {
        type = uint.MaxValue;
        for (uint i = 0; i < _memProps.MemoryTypeCount; i++)
        {
            if ((req.MemoryTypeBits & (1u << (int)i)) != 0 && (_memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
            {
                type = i;
                break;
            }
        }

        if (type == uint.MaxValue)   // exact match unavailable: fall back to any host-visible+coherent (readback/upload) or device-local
        {
            Vk.MemoryPropertyFlags relaxed = (props & Vk.MemoryPropertyFlags.HostVisibleBit) != 0
                ? Vk.MemoryPropertyFlags.HostVisibleBit | Vk.MemoryPropertyFlags.HostCoherentBit
                : Vk.MemoryPropertyFlags.DeviceLocalBit;
            for (uint i = 0; i < _memProps.MemoryTypeCount; i++)
            {
                if ((req.MemoryTypeBits & (1u << (int)i)) != 0 && (_memProps.MemoryTypes[(int)i].PropertyFlags & relaxed) == relaxed)
                {
                    type = i;
                    break;
                }
            }
        }

        if (type == uint.MaxValue)
        {
            throw new InvalidOperationException($"no Vulkan memory type for {props}");
        }

        var ai = new Vk.MemoryAllocateInfo { SType = Vk.StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = type };
        Check(Api.AllocateMemory(_device, &ai, null, out Vk.DeviceMemory mem), "AllocateMemory");
        return mem;
    }

    private void InitPsoCache()
    {
        byte[]? seed = _psoCacheSeed;
        fixed (byte* p = seed)
        {
            var ci = new Vk.PipelineCacheCreateInfo { SType = Vk.StructureType.PipelineCacheCreateInfo, InitialDataSize = (nuint)(seed?.Length ?? 0), PInitialData = seed is null ? null : p };
            _ = Api.CreatePipelineCache(_device, &ci, null, out _psoCache);
        }
    }
    private byte[]? _psoCacheSeed;
    /// <summary>The PSO cache handle — passed to graphics/compute pipeline creation; built once, hot path only binds.</summary>
    internal Vk.PipelineCache PsoCache => _psoCache;
    /// <summary>Serialize the pipeline cache to a blob for persistence (prewarm on next run).</summary>
    public byte[] GetPipelineCacheData()
    {
        nuint sz = 0;
        _ = Api.GetPipelineCacheData(_device, _psoCache, ref sz, null);
        byte[] data = new byte[(int)sz];
        if (sz > 0)
        {
            fixed (byte* p = data)
            {
                _ = Api.GetPipelineCacheData(_device, _psoCache, ref sz, p);
            }
        }

        return data;
    }
    /// <summary>Seed the pipeline cache from a previously serialized blob (call before pipeline creation).</summary>
    public void SeedPipelineCache(byte[] blob) { _psoCacheSeed = blob; }

    internal Vk.ShaderModule CreateShaderModule(byte[] spirv)
    {
        fixed (byte* p = spirv)
        {
            var ci = new Vk.ShaderModuleCreateInfo { SType = Vk.StructureType.ShaderModuleCreateInfo, CodeSize = (nuint)spirv.Length, PCode = (uint*)p };
            Check(Api.CreateShaderModule(_device, &ci, null, out Vk.ShaderModule m), "CreateShaderModule");
            return m;
        }
    }

    internal ICommandList AcquireCommandList(QueueKind kind)
    {
        var ai = new Vk.CommandBufferAllocateInfo { SType = Vk.StructureType.CommandBufferAllocateInfo, CommandPool = _cmdPool, Level = Vk.CommandBufferLevel.Primary, CommandBufferCount = 1 };
        Vk.CommandBuffer cb;
        Check(Api.AllocateCommandBuffers(_device, &ai, &cb), "AllocateCommandBuffers");
        var cl = new VulkanCommandList(this, cb, kind);
        lock (_gate)
        {
            _cmdLists.Add(cl);
        }

        return cl;
    }

    /// <inheritdoc />
    public IBuffer CreateBuffer(in BufferDesc desc)
    {
        return new VkBuffer(this, desc);
    }

    /// <inheritdoc />
    public ITexture CreateTexture(in TextureDesc desc)
    {
        return new VkTexture(this, desc);
    }

    /// <inheritdoc />
    public IBufferView CreateView(IBuffer buffer, in BufferViewDesc desc)
    {
        return new VkBufferViewRes(this, buffer, desc);
    }

    /// <inheritdoc />
    public ITextureView CreateView(ITexture texture, in TextureViewDesc desc)
    {
        return new VkTextureViewRes(this, (VkTexture)texture, desc);
    }

    /// <inheritdoc />
    public ISampler CreateSampler(in SamplerDesc desc)
    {
        return new VkSampler(this, desc);
    }

    /// <inheritdoc />
    public IComputeRecorder CreateComputeRecorder(ICommandList list)
    {
        return new Compute.VulkanComputeRecorder(this, (VulkanCommandList)list);
    }

    /// <inheritdoc />
    public IDescriptorHeap CreateDescriptorHeap(DescriptorKind kind, int capacity, bool shaderVisible)
    {
        return new VkDescriptorHeap(this, kind, capacity);
    }

    /// <inheritdoc />
    public IPipelineLayout CreatePipelineLayout(in PipelineLayoutDesc desc)
    {
        return new VkPipelineLayout(this, desc);
    }

    /// <inheritdoc />
    public IFence CreateFence(ulong initialValue)
    {
        return new VkFence(this, initialValue);
    }

    /// <inheritdoc />
    public IQueryHeap CreateTimestampQueryHeap(int capacity)
    {
        return new VkQueryHeap(this, capacity);
    }

    /// <inheritdoc />
    public void SetDebugName(IGpuResource resource, string name)
    {
#if MEWSTAGE_VK_MARKERS
        if (DebugUtils is null || string.IsNullOrEmpty(name) || !DeviceAlive)
        {
            return;
        }

        Vk.ObjectType type;
        ulong handle;
        switch (resource)
        {
            case VkBuffer b:
                type = Vk.ObjectType.Buffer;
                handle = b.Buffer.Handle;
                break;
            case VkTexture t:
                type = Vk.ObjectType.Image;
                handle = t.Image.Handle;
                break;
            default:
                return;
        }

        int n = System.Text.Encoding.UTF8.GetByteCount(name);
        Span<byte> buf = stackalloc byte[n + 1];
        System.Text.Encoding.UTF8.GetBytes(name, buf);
        buf[n] = 0;
        fixed (byte* pn = buf)
        {
            var info = new Vk.DebugUtilsObjectNameInfoEXT { SType = Vk.StructureType.DebugUtilsObjectNameInfoExt, ObjectType = type, ObjectHandle = handle, PObjectName = pn };
            DebugUtils.SetDebugUtilsObjectName(_device, &info);
        }
#endif
    }
    /// <inheritdoc />
    public ICommandSignature? CreateCommandSignature(in CommandSignatureDesc desc)
    {
        return null;
    }

    /// <inheritdoc />
    public IComputePipeline CreateComputePipeline(in ComputePipelineDesc desc)
    {
        return new VkComputePipeline(this, desc);
    }

    /// <inheritdoc />
    public IRenderPipeline CreateRenderPipeline(in RenderPipelineDesc desc)
    {
        return new VkRenderPipeline(this, desc);
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
            Lifetime.OnFrame(currentFrame, framesInFlight);   // the slot fence was waited: frames <= current - fif are done
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
        return DeviceStatus.Ok;
    }

    /// <inheritdoc />
    public event Action? OnDeviceLost;
    /// <inheritdoc />
    public bool TryRecreate()
    {
        // Device loss is non-fatal to the application. Tear down GPU objects and
        // re-init from the instance; callers re-upload static assets from the persistent render world.
        OnDeviceLost?.Invoke();
        try
        {
            _ = Api.DeviceWaitIdle(_device);
        }
        catch { /* device already lost */ }

        Lifetime.OnIdle();
        DestroyDevice();
        Init();
        return true;
    }

    internal static void Check(Vk.Result r, string what)
    {
        if (r == Vk.Result.Success)
        {
            return;
        }

        if (r == Vk.Result.ErrorDeviceLost)
        {
            GpuDeviceDiagnostics.Fault("VK_ERROR_DEVICE_LOST at " + what);
        }

        throw new InvalidOperationException($"Vulkan {what} failed: {r}");
    }

    private void DestroyDevice()
    {
        if (_device.Handle != 0)
        {
            _ = Api.DeviceWaitIdle(_device);
            lock (_gate)
            {
                _cmdLists.Clear();
            }

            if (_psoCache.Handle != 0)
            {
                Api.DestroyPipelineCache(_device, _psoCache, null);
                _psoCache = default;
            }

            Api.DestroyCommandPool(_device, _cmdPool, null);
            Api.DestroyDevice(_device, null);
            _device = default;
        }

        if (DebugUtils is not null && _debugMessenger.Handle != 0)
        {
            DebugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            _debugMessenger = default;
        }

        DebugUtils?.Dispose();
        DebugUtils = null;
        if (_instance.Handle != 0)
        {
            Api.DestroyInstance(_instance, null);
            _instance = default;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_device.Handle != 0)
        {
            try
            {
                _ = Api.DeviceWaitIdle(_device);
            }
            catch { }
        }

        Lifetime.OnIdle();
        for (int di = 0; di < _descAllocs.Length; di++)
        {
            _descAllocs[di]?.Dispose();
            _descAllocs[di] = null;
        }

        _ = FlushDeferred();
        DestroyDevice();
    }
}
