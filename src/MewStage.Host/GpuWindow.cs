// An SDL3 window on the application's GPU stack: the window, the device (Dx12Device / VulkanDevice), its windowed
// swapchain, the GpuScheduler and one RenderGraph, plus the per-frame bracket (BeginFrame: scheduler frame + acquire +
// graph reset; ImportBackbuffer; Submit + EndFrame: the present pass + submit + end frame) and the resize /
// per-monitor-DPI rule. Every mode of the demo opens, sizes, renders and tears down its window the same way.
//
// Teardown: Dispose drains the queue, then frees scheduler, swapchain, device (LAST of the GPU objects: no fence or
// semaphore wait can run after it), then the window and SDL. A caller that owns other GPU resources on this device
// calls WaitIdle, frees them, then disposes this.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using MewStage.Graphics;
using MewStage.Rendering.Backends;
using MewStage.Rendering.Backends.Dx12;
using MewStage.Rendering.Backends.Vulkan;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;

using SDL;

using static SDL.SDL3;

namespace MewStage.Host;

/// <summary>An SDL3 window with the application's device, swapchain, scheduler and render graph (see the file header).</summary>
public sealed unsafe class GpuWindow : IDisposable, IUiSurface
{
    /// <summary>
    /// The SDL video driver of an offscreen window (SDL3 src/video/offscreen: an event queue and window ids, no display).
    /// </summary>
    public const string OffscreenVideoDriver = "offscreen";

    // A display-scale change smaller than this is float noise from SDL, not a monitor change.
    private const float ScaleEpsilon = 1e-4f;

    private static readonly RgExecute NoopExec = static (in _) => { };

    [ThreadStatic]
    private static InitialPlacement? s_nextPlacement;

    private readonly bool _useWarp;
    private readonly Action<PassBuilder> _presentSetup;
    private RgHandle _presentTarget;
    private float _appliedScale = 1f;
    private bool _disposed;

    /// <param name="backend">The GPU API (see <see cref="GpuBackends.Select"/>).</param>
    /// <param name="useWarp">DX12 only: the WARP software adapter instead of the hardware one.</param>
    public GpuWindow(GpuBackend backend, bool useWarp = false)
    {
        Backend = backend;
        _useWarp = useWarp;
        _presentSetup = PresentSetup;
    }

    /// <summary>
    /// Where and how the next <see cref="Create"/> on this thread opens its window (absolute position in SDL window
    /// units; the size is Create's width / height, in pixels).
    /// </summary>
    public readonly record struct InitialPlacement(int X, int Y, bool Maximized);

    /// <summary>The GPU API this window renders with.</summary>
    public GpuBackend Backend { get; }

    /// <summary>The SDL window (null before <see cref="Create"/> and after <see cref="Dispose"/>).</summary>
    public SDL_Window* Window { get; private set; }

    /// <summary>The SDL window id (routes per-window events).</summary>
    public uint WindowId => Window == null ? 0 : (uint)SDL_GetWindowID(Window);

    /// <summary>The GPU device.</summary>
    public IGraphicsDevice Device { get; private set; } = null!;

    /// <summary>The window's swapchain.</summary>
    public ISwapchain Swapchain { get; private set; } = null!;

    /// <summary>The scheduler (frames in flight, command lists, the upload ring, deferred deletes).</summary>
    public GpuScheduler Scheduler { get; private set; } = null!;

    /// <summary>The render graph, rebuilt every frame between <see cref="BeginFrame"/> and <see cref="Submit"/>.</summary>
    public RenderGraph Graph { get; } = new();

    /// <summary>Backbuffer width, pixels.</summary>
    public int PixelWidth { get; private set; }

    /// <summary>Backbuffer height, pixels.</summary>
    public int PixelHeight { get; private set; }

    /// <summary>Window width in logical (OS) units: what the mouse coordinates of a hi-DPI window are in.</summary>
    public int LogicalWidth { get; private set; }

    /// <summary>Window height in logical (OS) units.</summary>
    public int LogicalHeight { get; private set; }

    /// <summary>The display's content scale (per-monitor DPI), refreshed by <see cref="RefreshMetrics"/>.</summary>
    public float DisplayScale { get; private set; } = 1f;

    /// <summary>What <see cref="WindowIcon.Apply(SDL_Window*)"/> did for this window.</summary>
    public WindowIcon.Result Icon { get; private set; }

    /// <summary>
    /// True once SDL_SetWindowIcon succeeded on this window (the OS shows its icon); false on SDL drivers without window
    /// icons (offscreen, dummy).
    /// </summary>
    public bool HasIcon => Icon.Accepted;

    /// <summary>True when the window was created OFFSCREEN (<see cref="Create"/> with offscreen: true).</summary>
    public bool IsOffscreen { get; private set; }

    /// <summary>
    /// Test seam: the display scale an OFFSCREEN window reports instead of SDL's 1 (the offscreen video driver has no
    /// display), so a lavapipe run lays out and rasterizes at, say, 125 %. Null = SDL's value. Ignored by a real window.
    /// </summary>
    public static float? OffscreenDisplayScaleOverride { get; set; }

    int IUiSurface.FramesInFlight => Scheduler.FramesInFlight;

    /// <summary>
    /// Asks the next <see cref="Create"/> on this thread to open the window at (<paramref name="x"/>, <paramref name="y"/>)
    /// (and maximized when asked), hidden until the device is ready. Consumed by that one call.
    /// </summary>
    public static void SetNextWindowPlacement(int x, int y, bool maximized)
    {
        s_nextPlacement = new InitialPlacement(x, y, maximized);
    }

    /// <summary>
    /// Initialize SDL (video plus <paramref name="extraInitFlags"/>, e.g. gamepads), open the window, start text input,
    /// and create the device, swapchain and scheduler. <paramref name="offscreen"/> (the journey harness on lavapipe):
    /// the SAME SDL event loop on SDL's offscreen video driver (events are pushed with SDL_PushEvent and polled by the
    /// host's real pump) and the same scheduler and render graph, over a headless Vulkan device whose swapchain is an
    /// <see cref="OffscreenSwapchain"/> (readable backbuffers). Vulkan only.
    /// </summary>
    public void Create(string title, int width, int height, SDL_InitFlags extraInitFlags = 0, bool offscreen = false)
    {
        if (offscreen && Backend != GpuBackend.Vulkan)
        {
            throw new InvalidOperationException("an offscreen GpuWindow needs the Vulkan backend (lavapipe)");
        }

        IsOffscreen = offscreen;
        if (offscreen)
        {
            SDL_SetHint(SDL_HINT_VIDEO_DRIVER, OffscreenVideoDriver);
        }

        if (!SDL_Init(SDL_InitFlags.SDL_INIT_VIDEO | extraInitFlags))
        {
            throw new InvalidOperationException("SDL_Init: " + SDL_GetError());
        }

        SDL_WindowFlags flags = SDL_WindowFlags.SDL_WINDOW_RESIZABLE | (Backend == GpuBackend.Vulkan && !offscreen ? SDL_WindowFlags.SDL_WINDOW_VULKAN : 0);

        // A requested placement (SetNextWindowPlacement): the window is created hidden at the wanted pixel size, moved
        // to the wanted position, and shown once the swapchain exists, so its first visible frame is already at the
        // right size and place.
        InitialPlacement? placement = s_nextPlacement;
        s_nextPlacement = null;
        bool placed = placement.HasValue && !offscreen;
        if (placed)
        {
            flags |= SDL_WindowFlags.SDL_WINDOW_HIDDEN | (placement!.Value.Maximized ? SDL_WindowFlags.SDL_WINDOW_MAXIMIZED : 0);
        }

        Window = SDL_CreateWindow(title, width, height, flags);
        if (Window == null)
        {
            throw new InvalidOperationException("SDL_CreateWindow: " + SDL_GetError());
        }

        if (placed)
        {
            SDL_SetWindowPosition(Window, placement!.Value.X, placement.Value.Y);
        }

        Icon = WindowIcon.Apply(Window);
        SDL_StartTextInput(Window);
        PixelWidth = width;
        PixelHeight = height;
        int lw;
        int lh;
        SDL_GetWindowSize(Window, &lw, &lh);
        LogicalWidth = lw > 0 ? lw : width;
        LogicalHeight = lh > 0 ? lh : height;

        if (offscreen)
        {
            Device = new VulkanDevice();
            GpuSchedulerOptions options = GpuSchedulerOptions.Default;
            Swapchain = CreateSwapchain(Window, Backend, Device, PixelWidth, PixelHeight, offscreen: true, options.FramesInFlight);
            Scheduler = new GpuScheduler(Device, Swapchain, options);
            return;
        }

        Device = Backend == GpuBackend.Dx12 ? new Dx12Device(_useWarp) : new VulkanDevice(enablePresentation: true);
        Swapchain = CreateSwapchain(Window, Backend, Device, PixelWidth, PixelHeight, offscreen: false, GpuSchedulerOptions.Default.FramesInFlight);
        Scheduler = new GpuScheduler(Device, Swapchain);
        if (placed)
        {
            SDL_ShowWindow(Window);
        }
    }

    /// <summary>
    /// The swapchain of <paramref name="window"/> on <paramref name="device"/>: the HWND's DXGI swapchain on DX12, a
    /// VkSurfaceKHR swapchain on Vulkan, and readable backbuffer textures (<see cref="OffscreenSwapchain"/>,
    /// <paramref name="framesInFlight"/> deep) for an offscreen window. The main window and every
    /// <see cref="SecondaryWindow"/> (its own swapchain on the main window's device) build theirs here.
    /// </summary>
    internal static ISwapchain CreateSwapchain(SDL_Window* window, GpuBackend backend, IGraphicsDevice device, int pixelWidth, int pixelHeight, bool offscreen, int framesInFlight)
    {
        if (offscreen)
        {
            return new OffscreenSwapchain(device, (uint)pixelWidth, (uint)pixelHeight, framesInFlight, Format.B8G8R8A8_UNorm);
        }

        if (backend == GpuBackend.Dx12)
        {
            SDL_PropertiesID props = SDL_GetWindowProperties(window);
            nint hwnd;
            fixed (byte* p = SDL_PROP_WINDOW_WIN32_HWND_POINTER)
            {
                hwnd = SDL_GetPointerProperty(props, p, IntPtr.Zero);
            }

            return new Dx12Swapchain((Dx12Device)device, hwnd, (uint)pixelWidth, (uint)pixelHeight);
        }

        var dev = (VulkanDevice)device;
        var inst = (VkInstance_T*)(nint)dev.InstanceHandle;
        VkSurfaceKHR_T* surf;
        if (!SDL_Vulkan_CreateSurface(window, inst, null, &surf))
        {
            throw new InvalidOperationException("SDL_Vulkan_CreateSurface: " + SDL_GetError());
        }

        return new VulkanSwapchain(dev, (ulong)(nint)surf, (uint)pixelWidth, (uint)pixelHeight);
    }

    /// <summary>
    /// Refresh the logical window size and the display scale (call once per frame after pumping events, so a move to
    /// a monitor with another scale is seen before the next <see cref="EnsureSize"/>).
    /// </summary>
    public void RefreshMetrics()
    {
        int lw;
        int lh;
        SDL_GetWindowSize(Window, &lw, &lh);
        if (lw > 0 && lh > 0)
        {
            LogicalWidth = lw;
            LogicalHeight = lh;
        }

        float s = IsOffscreen && OffscreenDisplayScaleOverride is float forced ? forced : SDL_GetWindowDisplayScale(Window);
        DisplayScale = s > 0f ? s : 1f;
    }

    /// <summary>
    /// Rebuild the swapchain when the pixel size changed, and report a size OR display-scale change (the UI surface
    /// must be rebuilt on either). Drains the queue first: the scheduler is FramesInFlight deep and an in-flight command
    /// list may still reference the resources being replaced. True when the caller must resize its own surfaces.
    /// </summary>
    public bool EnsureSize()
    {
        int pw;
        int ph;
        SDL_GetWindowSizeInPixels(Window, &pw, &ph);
        bool dpiChanged = Math.Abs(DisplayScale - _appliedScale) > ScaleEpsilon;
        bool sizeChanged = pw > 0 && ph > 0 && (pw != PixelWidth || ph != PixelHeight);
        if (pw <= 0 || ph <= 0 || (!sizeChanged && !dpiChanged))
        {
            return false;
        }

        WaitIdle();
        if (sizeChanged)
        {
            Swapchain.Resize((uint)pw, (uint)ph);
            PixelWidth = pw;
            PixelHeight = ph;
        }

        _appliedScale = DisplayScale;
        return true;
    }

    /// <summary>Start a frame: the scheduler's frame, the next backbuffer, an empty render graph.</summary>
    public FrameContext BeginFrame(out ITexture backbuffer)
    {
        FrameContext ctx = Scheduler.BeginFrame();
        backbuffer = Swapchain.AcquireNextBackbuffer(out _);
        Graph.Reset();
        return ctx;
    }

    /// <summary>Import <paramref name="backbuffer"/> into this frame's graph as its output (once per frame).</summary>
    public RgHandle ImportBackbuffer(ITexture backbuffer)
    {
        ArgumentNullException.ThrowIfNull(backbuffer);
        return Graph.ImportTexture(
            new RgTextureDesc { Name = "backbuffer", Format = Swapchain.Format, Width = (uint)PixelWidth, Height = (uint)PixelHeight, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 },
            backbuffer.Handle,
            ResourceState.Common,
            isGraphOutput: true);
    }

    /// <summary>End the frame: transition <paramref name="backbuffer"/> to Present, submit the graph, end the frame.</summary>
    public void SubmitAndPresent(FrameContext ctx, RgHandle backbuffer)
    {
        Submit(ctx, backbuffer);
        Scheduler.EndFrame(ctx);
    }

    /// <summary>
    /// The first half of <see cref="SubmitAndPresent"/>: transition <paramref name="backbuffer"/> to Present and submit
    /// the graph (this window's acquire / present sync rides on this submit). A host with secondary windows submits
    /// their graphs after this and before <see cref="EndFrame"/>, so the frame fence covers them.
    /// </summary>
    public void Submit(FrameContext ctx, RgHandle backbuffer)
    {
        _presentTarget = backbuffer;
        Graph.AddPass("present", PassKind.Graphics, _presentSetup);
        Scheduler.Submit(Graph, ctx);
    }

    /// <summary>The second half of <see cref="SubmitAndPresent"/>: present this window and end the scheduler frame.</summary>
    public void EndFrame(FrameContext ctx)
    {
        Scheduler.EndFrame(ctx);
    }

    /// <summary>Wait for the GPU to finish all submitted work (best effort: a lost device is not an error here).</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A lost device throws a backend-specific exception; teardown and resize carry on regardless.")]
    public void WaitIdle()
    {
        try
        {
            Device?.GraphicsQueue.WaitIdle();
        }
        catch (Exception)
        {
            // The device may already be lost; the caller is tearing down or rebuilding anyway.
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        WaitIdle();
        Scheduler?.Dispose();
        Swapchain?.Dispose();
        (Device as IDisposable)?.Dispose();   // device LAST of the GPU objects
        if (Window != null)
        {
            SDL_DestroyWindow(Window);
            Window = null;
        }

        SDL_Quit();
    }

    /// <summary>A one-line description for logs: backend, size, scale.</summary>
    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{Backend} {PixelWidth}x{PixelHeight} @ {DisplayScale:F2}{(IsOffscreen ? " (offscreen)" : string.Empty)}");
    }

    private void PresentSetup(PassBuilder s)
    {
        // An offscreen swapchain never presents: its backbuffer stays in the state the next import declares (Common).
        // PRESENT_SRC is a swapchain-extension layout, invalid on a device created without presentation.
        s.Write(_presentTarget, IsOffscreen ? ResourceState.Common : ResourceState.Present, ShaderStage.None);
        s.SetExecute(NoopExec);
    }
}
