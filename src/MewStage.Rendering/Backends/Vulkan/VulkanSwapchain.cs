// Windowed Vulkan swapchain — MewStage.Rendering. Specializes ISwapchain over a
// VkSwapchainKHR created from a window VkSurfaceKHR (the Presentation host creates the surface via SDL3 —
// SDL_Vulkan_CreateSurface — and passes its handle here). Present-mode policy: FIFO default,
// MAILBOX for VRR. Handles VK_ERROR_OUT_OF_DATE_KHR by recreating (resize / device-loss). Backbuffer images are
// wrapped as external VkTextures the render graph imports as its output. RUNTIME is Windows/Linux-display only
// (no surface in the headless sandbox) ; compile + AOT-clean everywhere; runtime-verified on Windows and Linux.

using System;

using MewStage.Graphics;
using MewStage.Graphics.Diagnostics;

using Silk.NET.Vulkan.Extensions.KHR;

using Vk = Silk.NET.Vulkan;

namespace MewStage.Rendering.Backends.Vulkan;

/// <summary>A VkSwapchainKHR over a window surface, with binary acquire / present semaphores attached to the frame's
/// one presenting submit (<see cref="IPresentSyncSubmit"/>).</summary>
public sealed unsafe class VulkanSwapchain : ISwapchain
{
    private readonly VulkanDevice _dev;
    private readonly KhrSurface _khrSurface;
    private readonly KhrSwapchain _khrSwap;
    private Vk.SurfaceKHR _surface;
    private Vk.SwapchainKHR _swapchain;
    private VkTexture[] _backbuffers = [];
    private Vk.Semaphore[] _acquireSems = [];      // ring, size imageCount+1
    private Vk.Semaphore[] _renderFinishedSems = []; // per swapchain image
    private int _acquireCursor;
    private uint _imageIndex;

    /// <inheritdoc />
    public int BackbufferCount => _backbuffers.Length;
    /// <inheritdoc />
    public Format Format { get; }
    /// <inheritdoc />
    public uint Width { get; private set; }
    /// <inheritdoc />
    public uint Height { get; private set; }
    /// <inheritdoc />
    public PresentMode PresentMode { get; }

    /// <summary>Creates the swapchain over a surface.</summary>
    /// <param name="device">The device.</param>
    /// <param name="surfaceHandle">A VkSurfaceKHR the host created from its window (SDL_Vulkan_CreateSurface).</param>
    /// <param name="width">Width in pixels.</param>
    /// <param name="height">Height in pixels.</param>
    /// <param name="format">Backbuffer format.</param>
    /// <param name="presentMode">Preferred present mode.</param>
    public VulkanSwapchain(VulkanDevice device, ulong surfaceHandle, uint width, uint height,
        Format format = Format.B8G8R8A8_UNorm, PresentMode presentMode = PresentMode.Fifo)
    {
        _dev = device ?? throw new ArgumentNullException(nameof(device));
        Format = format;
        PresentMode = presentMode;
        Width = width;
        Height = height;
        _surface = new Vk.SurfaceKHR(surfaceHandle);
        if (!_dev.Api.TryGetInstanceExtension(_dev.Instance, out _khrSurface))
        {
            throw new InvalidOperationException("VK_KHR_surface not available");
        }

        if (!_dev.Api.TryGetDeviceExtension(_dev.Instance, _dev.Device, out _khrSwap))
        {
            throw new InvalidOperationException("VK_KHR_swapchain not available");
        }

        Create();
    }

    private void Create()
    {
        _ = _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_dev.PhysicalDevice, _surface, out Vk.SurfaceCapabilitiesKHR caps);

        // Present mode: FIFO (the requested default) is hard vsync on a DOUBLE-buffered swapchain — the CPU blocks
        // on acquire until the single back image is free, so any late frame drops the whole pipeline to the next
        // vblank => visible judder even at a nominal 60 (this is the VK-vs-DX12 gap: DX12 uses a low-latency flip
        // model). Prefer MAILBOX when the surface supports it (triple-buffered, tear-free, newest-frame-wins — the
        // flip-model equivalent); fall back to FIFO_RELAXED, then FIFO (always supported). An explicit Immediate
        // request is honored if available. Env MEWSTAGE_VK_PRESENT = fifo|mailbox|immediate forces a mode (diagnostics).
        uint pmCount = 0;
        _ = _khrSurface.GetPhysicalDeviceSurfacePresentModes(_dev.PhysicalDevice, _surface, ref pmCount, null);
        Vk.PresentModeKHR* pmodes = stackalloc Vk.PresentModeKHR[(int)Math.Max(1u, pmCount)];
        _ = _khrSurface.GetPhysicalDeviceSurfacePresentModes(_dev.PhysicalDevice, _surface, ref pmCount, pmodes);
        bool Has(Vk.PresentModeKHR m)
        {
            for (uint i = 0; i < pmCount; i++)
            {
                if (pmodes[i] == m)
                {
                    return true;
                }
            }

            return false;
        }

        Vk.PresentModeKHR present;
        string? force = Environment.GetEnvironmentVariable("MEWSTAGE_VK_PRESENT")?.Trim().ToLowerInvariant();
        if (force == "fifo")
        {
            present = Vk.PresentModeKHR.FifoKhr;
        }
        else if (force == "mailbox" && Has(Vk.PresentModeKHR.MailboxKhr))
        {
            present = Vk.PresentModeKHR.MailboxKhr;
        }
        else
        {
            present = force == "immediate" && Has(Vk.PresentModeKHR.ImmediateKhr)
            ? Vk.PresentModeKHR.ImmediateKhr
            : PresentMode switch
            {
                PresentMode.Immediate when Has(Vk.PresentModeKHR.ImmediateKhr) => Vk.PresentModeKHR.ImmediateKhr,
                PresentMode.Mailbox when Has(Vk.PresentModeKHR.MailboxKhr) => Vk.PresentModeKHR.MailboxKhr,
                // DEFAULT: vsync-locked FIFO (triple-buffered via the image-count below) — steady vblank cadence that
                // matches the DX12 flip-model's smoothness. MAILBOX is lower-latency but throttle-paced (variable GPU
                // time => uneven delivery / choppy here); opt in with MEWSTAGE_VK_PRESENT=mailbox.
                _ => Vk.PresentModeKHR.FifoKhr,   // always supported
            };
        }

        // Triple-buffer: minImageCount+1 lets the CPU keep FramesInFlight frames queued without stalling on acquire
        // (required for MAILBOX to actually pipeline; also smooths FIFO). Clamp to the surface max.
        uint desired = Math.Max(caps.MinImageCount + 1u, 3u);   // triple-buffer (FIFO or MAILBOX): keep FramesInFlight queued
        if (caps.MaxImageCount != 0)
        {
            desired = Math.Min(desired, caps.MaxImageCount);
        }

        var ci = new Vk.SwapchainCreateInfoKHR
        {
            SType = Vk.StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = desired,
            ImageFormat = VulkanMap.Format(Format),
            ImageColorSpace = Vk.ColorSpaceKHR.SpaceSrgbNonlinearKhr,
            ImageExtent = new Vk.Extent2D(Width, Height),
            ImageArrayLayers = 1,
            ImageUsage = Vk.ImageUsageFlags.ColorAttachmentBit | Vk.ImageUsageFlags.TransferDstBit,
            ImageSharingMode = Vk.SharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = Vk.CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = present,
            Clipped = true,
        };
        VulkanDevice.Check(_khrSwap.CreateSwapchain(_dev.Device, &ci, null, out _swapchain), "CreateSwapchain");

        uint n = 0;
        _ = _khrSwap.GetSwapchainImages(_dev.Device, _swapchain, ref n, null);
        Vk.Image* images = stackalloc Vk.Image[(int)n];
        _ = _khrSwap.GetSwapchainImages(_dev.Device, _swapchain, ref n, images);
        _backbuffers = new VkTexture[n];
        var desc = new TextureDesc { Dimension = TextureDimension.Tex2D, Format = Format, Width = Width, Height = Height, DepthOrArrayLayers = 1, MipLevels = 1, SampleCount = 1, Usage = ResourceUsage.RenderTarget };
        for (uint i = 0; i < n; i++)
        {
            _backbuffers[i] = new VkTexture(_dev, images[i], desc);
        }

        // Presentation binary semaphores. Acquire ring is imageCount+1 so an unsignaled acquire
        // semaphore is always available (the render submit that waits it may still be queued when the next frame
        // acquires). Render-finished is PER-IMAGE: present waits the semaphore the submit that rendered THAT image
        // signaled, so a semaphore is never reused before its present has consumed it.
        var sci = new Vk.SemaphoreCreateInfo { SType = Vk.StructureType.SemaphoreCreateInfo };
        _acquireSems = new Vk.Semaphore[n + 1];
        for (int i = 0; i < _acquireSems.Length; i++)
        {
            _ = _dev.Api.CreateSemaphore(_dev.Device, &sci, null, out _acquireSems[i]);
        }

        _renderFinishedSems = new Vk.Semaphore[n];
        for (int i = 0; i < _renderFinishedSems.Length; i++)
        {
            _ = _dev.Api.CreateSemaphore(_dev.Device, &sci, null, out _renderFinishedSems[i]);
        }

        _acquireCursor = 0;
    }

    /// <inheritdoc />
    public ITexture AcquireNextBackbuffer(out int backbufferIndex)
    {
        using ProfileScope zone = Profiler.BeginZone("VulkanSwapchain.AcquireNextBackbuffer");

        Vk.Semaphore acq = _acquireSems[_acquireCursor];
        _acquireCursor = (_acquireCursor + 1) % _acquireSems.Length;
        uint idx = 0;
        Vk.Result r = _khrSwap.AcquireNextImage(_dev.Device, _swapchain, ulong.MaxValue, acq, default, ref idx);
        if (r == Vk.Result.ErrorOutOfDateKhr)
        {
            Recreate();                                   // rebuilds the semaphore arrays; re-pick from the NEW ring
            acq = _acquireSems[_acquireCursor];
            _acquireCursor = (_acquireCursor + 1) % _acquireSems.Length;
            _ = _khrSwap.AcquireNextImage(_dev.Device, _swapchain, ulong.MaxValue, acq, default, ref idx);
        }

        _imageIndex = idx;
        backbufferIndex = (int)idx;
        _dev.RegisterPresentSync(acq, _renderFinishedSems[idx]);   // render submit waits acq, signals renderFinished[idx]
        return _backbuffers[idx];
    }

    /// <inheritdoc />
    public void Present(int syncInterval)
    {
        using ProfileScope zone = Profiler.BeginZone("VulkanSwapchain.Present");

        _dev.SubmitUnclaimedPresentSync();   // no-op when the frame's render submit (SubmitFrame) took the sync
        Vk.SwapchainKHR sc = _swapchain;
        uint idx = _imageIndex;
        Vk.Semaphore wait = _renderFinishedSems[idx];
        var pi = new Vk.PresentInfoKHR { SType = Vk.StructureType.PresentInfoKhr, SwapchainCount = 1, PSwapchains = &sc, PImageIndices = &idx, WaitSemaphoreCount = 1, PWaitSemaphores = &wait };
        Vk.Result r = _khrSwap.QueuePresent(_dev.GraphicsQueueRaw, &pi);
        if (r is Vk.Result.ErrorOutOfDateKhr or Vk.Result.SuboptimalKhr)
        {
            Recreate();
        }
    }

    /// <inheritdoc />
    public void Resize(uint width, uint height)
    {
        Width = width;
        Height = height;
        Recreate();
    }

    private void Recreate()
    {
        using ProfileScope zone = Profiler.BeginZone("VulkanSwapchain.Recreate");

        _ = _dev.Api.DeviceWaitIdle(_dev.Device);
        DestroyBackbuffers();
        Create();
    }

    private void DestroyBackbuffers()
    {
        foreach (VkTexture t in _backbuffers)
        {
            t.Dispose();
        }

        _backbuffers = [];
        foreach (Vk.Semaphore sm in _acquireSems)
        {
            if (sm.Handle != 0)
            {
                _dev.Api.DestroySemaphore(_dev.Device, sm, null);
            }
        }

        _acquireSems = [];
        foreach (Vk.Semaphore sm in _renderFinishedSems)
        {
            if (sm.Handle != 0)
            {
                _dev.Api.DestroySemaphore(_dev.Device, sm, null);
            }
        }

        _renderFinishedSems = [];
        if (_swapchain.Handle != 0)
        {
            _khrSwap.DestroySwapchain(_dev.Device, _swapchain, null);
            _swapchain = default;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The swapchain sync objects are BINARY semaphores created directly here (NOT VkResource), so the device-alive
        // guard on VkResource.Dispose does not cover them. vkDestroyDevice implicitly frees all child objects, so
        // destroying them after the device is gone is invalid (VUID-vkDestroySemaphore-device-parameter). Guard them.
        if (_dev.DeviceAlive)
        {
            DestroyBackbuffers();   // frees backbuffers + acquire/render-finished semaphores
        }

        if (_dev.Instance.Handle != 0 && _surface.Handle != 0)
        {
            _khrSurface.DestroySurface(_dev.Instance, _surface, null);
            _surface = default;
        }
    }
}
