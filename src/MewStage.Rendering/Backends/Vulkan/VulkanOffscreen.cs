// The Vulkan 1.3 offscreen render proof.
//
// Exercises the real Vulkan 1.3 backend mechanics of the portable baseline: an instance at API 1.3, a graphics queue,
// device-local image allocation, DYNAMIC RENDERING (core 1.3, no VkRenderPass / VkFramebuffer), SYNCHRONIZATION2 image
// barriers, command submission + a fence wait, and an image-to-host-visible readback. Runs headless against Mesa
// lavapipe in CI (no surface, no swapchain), so the whole device / memory / sync / submit path is verified on a real
// Vulkan implementation. The full VulkanDevice is built on exactly these primitives; this is the smoke harness.
// NativeAOT-safe: Silk.NET is source-generated P/Invoke; no reflection.

using System;

using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace MewStage.Rendering.Backends.Vulkan;

/// <summary>Result of an offscreen render: tightly-packed RGBA8 pixels + dimensions.</summary>
public readonly struct OffscreenImage
{
    /// <summary>Creates the record.</summary>
    /// <param name="rgba">Tightly packed RGBA8 pixels, row-major.</param>
    /// <param name="w">Width in pixels.</param>
    /// <param name="h">Height in pixels.</param>
    public OffscreenImage(byte[] rgba, int w, int h)
    {
        Rgba = rgba;
        Width = w;
        Height = h;
    }

    /// <summary>Tightly packed RGBA8 pixels, row-major.</summary>
    public byte[] Rgba { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The pixel at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    /// <returns>The channel values.</returns>
    public (byte R, byte G, byte B, byte A) Pixel(int x, int y)
    {
        int i = ((y * Width) + x) * 4;
        return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
    }
}

/// <summary>
/// Self-contained Vulkan 1.3 offscreen renderer. <see cref="ClearAndReadback"/> clears a device-local image to
/// a known color via dynamic rendering and reads it back — the minimal proof of the device/memory/sync2/submit
/// path on lavapipe.
/// </summary>
public static unsafe class VulkanOffscreen
{
    /// <summary>Whether a Vulkan 1.3 implementation with a graphics queue can be created on this machine.</summary>
    /// <param name="detail">The device name, or the failure reason.</param>
    /// <returns>True when offscreen rendering is possible.</returns>
    public static bool IsAvailable(out string detail)
    {
        try
        {
            var vk = Vk.GetApi();
            byte* appName = (byte*)SilkMarshal.StringToPtr("MewStage-probe");
            var ai = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = appName,
                ApiVersion = Vk.Version13,
            };
            var ici = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &ai };
            if (vk.CreateInstance(&ici, null, out Instance inst) != Result.Success)
            {
                _ = SilkMarshal.Free((nint)appName);
                detail = "vkCreateInstance failed";
                return false;
            }

            uint n = 0;
            _ = vk.EnumeratePhysicalDevices(inst, ref n, null);
            vk.DestroyInstance(inst, null);
            _ = SilkMarshal.Free((nint)appName);
            detail = $"{n} physical device(s)";
            return n > 0;
        }
        catch (Exception e)
        {
            detail = e.Message;
            return false;
        }
    }

    /// <summary>Clears a device-local image to a color through dynamic rendering and reads it back.</summary>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <param name="a">Alpha.</param>
    /// <returns>The image read back.</returns>
    public static OffscreenImage ClearAndReadback(int width, int height, float r, float g, float b, float a = 1f)
    {
        var vk = Vk.GetApi();
        byte* appName = (byte*)SilkMarshal.StringToPtr("MewStage");
        Instance instance = default;
        Device device = default;
        try
        {
            // --- instance @ Vulkan 1.3 ---
            var ai = new ApplicationInfo { SType = StructureType.ApplicationInfo, PApplicationName = appName, PEngineName = appName, ApiVersion = Vk.Version13 };
            var ici = new InstanceCreateInfo { SType = StructureType.InstanceCreateInfo, PApplicationInfo = &ai };
            Check(vk.CreateInstance(&ici, null, out instance), "CreateInstance");

            // --- pick physical device + a graphics queue family ---
            uint pdCount = 0;
            _ = vk.EnumeratePhysicalDevices(instance, ref pdCount, null);
            if (pdCount == 0)
            {
                throw new InvalidOperationException("no Vulkan physical devices");
            }

            PhysicalDevice* pds = stackalloc PhysicalDevice[(int)pdCount];
            _ = vk.EnumeratePhysicalDevices(instance, ref pdCount, pds);
            PhysicalDevice phys = pds[0];
            uint qfCount = 0;
            vk.GetPhysicalDeviceQueueFamilyProperties(phys, ref qfCount, null);
            QueueFamilyProperties* qfs = stackalloc QueueFamilyProperties[(int)qfCount];
            vk.GetPhysicalDeviceQueueFamilyProperties(phys, ref qfCount, qfs);
            uint gfxFamily = uint.MaxValue;
            for (uint i = 0; i < qfCount; i++)
            {
                if ((qfs[i].QueueFlags & QueueFlags.GraphicsBit) != 0)
                {
                    gfxFamily = i;
                    break;
                }
            }

            if (gfxFamily == uint.MaxValue)
            {
                throw new InvalidOperationException("no graphics queue family");
            }

            // --- logical device: enable dynamicRendering + synchronization2 + timelineSemaphore (1.3 baseline) ---
            float priority = 1f;
            var qci = new DeviceQueueCreateInfo { SType = StructureType.DeviceQueueCreateInfo, QueueFamilyIndex = gfxFamily, QueueCount = 1, PQueuePriorities = &priority };
            var f13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features, DynamicRendering = true, Synchronization2 = true };
            var f12 = new PhysicalDeviceVulkan12Features { SType = StructureType.PhysicalDeviceVulkan12Features, TimelineSemaphore = true, PNext = &f13 };
            var dci = new DeviceCreateInfo { SType = StructureType.DeviceCreateInfo, PNext = &f12, QueueCreateInfoCount = 1, PQueueCreateInfos = &qci };
            Check(vk.CreateDevice(phys, &dci, null, out device), "CreateDevice");
            vk.GetDeviceQueue(device, gfxFamily, 0, out Queue queue);

            const Format Fmt = Format.R8G8B8A8Unorm;

            // --- offscreen color image (device-local) ---
            var imgCi = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = Fmt,
                Extent = new Extent3D((uint)width, (uint)height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(vk.CreateImage(device, &imgCi, null, out Image image), "CreateImage");
            vk.GetImageMemoryRequirements(device, image, out MemoryRequirements imgReq);
            DeviceMemory imgMem = AllocMemory(vk, phys, device, imgReq, MemoryPropertyFlags.DeviceLocalBit);
            _ = vk.BindImageMemory(device, image, imgMem, 0);

            var ivCi = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = Fmt,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            Check(vk.CreateImageView(device, &ivCi, null, out ImageView view), "CreateImageView");

            // --- host-visible readback buffer ---
            ulong rbSize = (ulong)(width * height * 4);
            var bCi = new BufferCreateInfo { SType = StructureType.BufferCreateInfo, Size = rbSize, Usage = BufferUsageFlags.TransferDstBit, SharingMode = SharingMode.Exclusive };
            Check(vk.CreateBuffer(device, &bCi, null, out Silk.NET.Vulkan.Buffer readback), "CreateBuffer");
            vk.GetBufferMemoryRequirements(device, readback, out MemoryRequirements bReq);
            DeviceMemory rbMem = AllocMemory(vk, phys, device, bReq, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCachedBit);
            _ = vk.BindBufferMemory(device, readback, rbMem, 0);

            // --- command pool + buffer ---
            var poolCi = new CommandPoolCreateInfo { SType = StructureType.CommandPoolCreateInfo, QueueFamilyIndex = gfxFamily };
            Check(vk.CreateCommandPool(device, &poolCi, null, out CommandPool pool), "CreateCommandPool");
            var cbAi = new CommandBufferAllocateInfo { SType = StructureType.CommandBufferAllocateInfo, CommandPool = pool, Level = CommandBufferLevel.Primary, CommandBufferCount = 1 };
            CommandBuffer cmd;
            Check(vk.AllocateCommandBuffers(device, &cbAi, &cmd), "AllocateCommandBuffers");

            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            Check(vk.BeginCommandBuffer(cmd, &begin), "BeginCommandBuffer");

            var fullRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            // sync2 barrier: Undefined -> ColorAttachmentOptimal
            Barrier2(vk, cmd, image, fullRange, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
                PipelineStageFlags2.TopOfPipeBit, AccessFlags2.None,
                PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit);

            var clear = new ClearValue(new ClearColorValue(r, g, b, a));
            var colorAtt = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = view,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = clear,
            };
            var renderInfo = new RenderingInfo
            {
                SType = StructureType.RenderingInfo,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)width, (uint)height)),
                LayerCount = 1,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorAtt,
            };
            vk.CmdBeginRendering(cmd, &renderInfo);
            vk.CmdEndRendering(cmd);

            // sync2 barrier: ColorAttachmentOptimal -> TransferSrcOptimal
            Barrier2(vk, cmd, image, fullRange, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
                PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);

            var copy = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D((uint)width, (uint)height, 1),
            };
            vk.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, readback, 1, &copy);

            Check(vk.EndCommandBuffer(cmd), "EndCommandBuffer");

            // --- submit + fence wait ---
            var fenceCi = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Check(vk.CreateFence(device, &fenceCi, null, out Fence fence), "CreateFence");
            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cmd };
            Check(vk.QueueSubmit(queue, 1, &submit, fence), "QueueSubmit");
            Check(vk.WaitForFences(device, 1, &fence, true, ulong.MaxValue), "WaitForFences");

            // --- readback ---
            byte[] rgba = new byte[rbSize];
            void* mapped;
            Check(vk.MapMemory(device, rbMem, 0, rbSize, 0, &mapped), "MapMemory");
            fixed (byte* dst = rgba)
            {
                System.Buffer.MemoryCopy(mapped, dst, rbSize, rbSize);
            }

            vk.UnmapMemory(device, rbMem);

            // --- teardown ---
            vk.DestroyFence(device, fence, null);
            vk.DestroyCommandPool(device, pool, null);
            vk.DestroyBuffer(device, readback, null);
            vk.FreeMemory(device, rbMem, null);
            vk.DestroyImageView(device, view, null);
            vk.DestroyImage(device, image, null);
            vk.FreeMemory(device, imgMem, null);
            return new OffscreenImage(rgba, width, height);
        }
        finally
        {
            if (device.Handle != 0)
            {
                vk.DestroyDevice(device, null);
            }

            if (instance.Handle != 0)
            {
                vk.DestroyInstance(instance, null);
            }

            _ = SilkMarshal.Free((nint)appName);
        }
    }

    private static void Barrier2(Vk vk, CommandBuffer cmd, Image image, ImageSubresourceRange range,
        ImageLayout oldLayout, ImageLayout newLayout,
        PipelineStageFlags2 srcStage, AccessFlags2 srcAccess, PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
    {
        var b = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            Image = image,
            SubresourceRange = range,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        };
        var dep = new DependencyInfo { SType = StructureType.DependencyInfo, ImageMemoryBarrierCount = 1, PImageMemoryBarriers = &b };
        vk.CmdPipelineBarrier2(cmd, &dep);
    }

    private static DeviceMemory AllocMemory(Vk vk, PhysicalDevice phys, Device device, MemoryRequirements req, MemoryPropertyFlags props)
    {
        vk.GetPhysicalDeviceMemoryProperties(phys, out PhysicalDeviceMemoryProperties mp);
        uint typeIndex = uint.MaxValue;
        for (uint i = 0; i < mp.MemoryTypeCount; i++)
        {
            if ((req.MemoryTypeBits & (1u << (int)i)) != 0 && (mp.MemoryTypes[(int)i].PropertyFlags & props) == props)
            {
                typeIndex = i;
                break;
            }
        }

        if (typeIndex == uint.MaxValue)
        {
            throw new InvalidOperationException($"no memory type for {props}");
        }

        var ai = new MemoryAllocateInfo { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = typeIndex };
        Check(vk.AllocateMemory(device, &ai, null, out DeviceMemory mem), "AllocateMemory");
        return mem;
    }

    private static void Check(Result r, string what)
    {
        if (r != Result.Success)
        {
            throw new InvalidOperationException($"Vulkan {what} failed: {r}");
        }
    }
}
