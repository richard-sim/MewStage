// A second OS window of an AppHost: its own SDL window and its own swapchain on the SAME device, rendered INSIDE the
// owner's frame, on the owner's GpuScheduler:
//   * the owner's pump routes every SDL event that names this window (keyboard, text, mouse, wheel, pen, window events)
//     into THIS window's RawInputSnapshot; the OS close button sets CloseRequested (the application decides: it closes
//     the window at its frame boundary);
//   * BeginFrame (after the owner's input step): metrics, this window's own input bridge dispatch into its MewUI Window
//     (its own UiLayer, so popups - dropdown lists, menus, tooltips - open in THIS window's surface), resize;
//   * Render (after the owner's graph was submitted, before the owner ends the frame): acquire this swapchain's
//     backbuffer, record this window's `ui` pass into its own RenderGraph, submit it on the owner's scheduler and
//     present. Acquire -> submit keeps Vulkan's one pending acquire / present semaphore pair per submit correct, and the
//     owner's frame fence then covers this window's command list, upload ring and transient rentals, so the scheduler's
//     frames-in-flight throttle and deferred deletes hold.
// The owner window is the OS owner (SDL_PROP_WINDOW_CREATE_PARENT_POINTER at creation, SDL_SetWindowParent if a driver
// dropped it: an owned window on Win32, a transient-for window on X11): it stays above the owner and is destroyed with
// it. Modeless: the owner keeps rendering and taking input. Offscreen (the journey harness on lavapipe): the same
// window on SDL's offscreen driver, with an OffscreenSwapchain (readable backbuffers) on the owner's headless Vulkan
// device. Steady-state frames allocate nothing (cached pass delegates, a reused graph, per-frame state in fields).
using System;

using MewStage.Graphics;
using MewStage.Input;
using MewStage.Rendering.Graph;
using MewStage.Rendering.Scheduler;

using SDL;

using static SDL.SDL3;

namespace MewStage.Host;

/// <summary>
/// A second OS window of an <see cref="AppHost"/> (see the file header). Open it with <see cref="AppHost.OpenSecondary"/>,
/// show MewUI content with <see cref="Show"/>, close it with <see cref="AppHost.CloseSecondary"/>.
/// </summary>
public sealed unsafe class SecondaryWindow : IDisposable, IUiSurface
{
    // Present interval of a secondary window: the owner's present already paces the frame to the display (vsync); a
    // second synchronized present in the same frame would wait for another vertical blank and halve the frame rate.
    // 0 = present without waiting (flip model: the newest frame is shown at the next blank, no tearing).
    private const int PresentSyncInterval = 0;

    // A display-scale change smaller than this is float noise from SDL, not a monitor change (as GpuWindow).
    private const float ScaleEpsilon = 1e-4f;

    private static readonly RgExecute NoopExec = static (in _) => { };
    private readonly Action<PassBuilder> _presentSetup;
    private readonly Action<PassBuilder> _clearSetup;
    private readonly RgExecute _clearExec;
    private RgHandle _bb;
    private float _appliedScale = 1f;
    private int _x;
    private int _y;
    private bool _disposed;

    internal SecondaryWindow(AppHost host, string title, int x, int y, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        Owner = host.Gpu;
        Title = title;
        _presentSetup = PresentSetup;
        _clearSetup = ClearSetup;
        _clearExec = ClearExecute;
        bool vulkanSurface = Owner.Backend == GpuBackend.Vulkan && !Owner.IsOffscreen;
        Window = CreateWindowAt(title, x, y, width, height, vulkanSurface, Owner.Window);
        if (Window == null)
        {
            throw new InvalidOperationException("SDL_CreateWindowWithProperties (" + title + "): " + SDL_GetError());
        }

        try
        {
            // The owner is given at creation (every driver keeps SDL's window hierarchy; Win32 / X11 make it the OS
            // owner); a driver that dropped it is asked once more, and a window without its owner is an error.
            if (SDL_GetWindowParent(Window) != Owner.Window && !SDL_SetWindowParent(Window, Owner.Window))
            {
                throw new InvalidOperationException("SDL_SetWindowParent (" + title + "): " + SDL_GetError());
            }

            Icon = WindowIcon.Apply(Window);
            SDL_StartTextInput(Window);
            WindowId = (uint)SDL_GetWindowID(Window);
            int px0;
            int py0;
            SDL_GetWindowPosition(Window, &px0, &py0);
            _x = px0;
            _y = py0;
            RefreshMetrics();
            int pw;
            int ph;
            SDL_GetWindowSizeInPixels(Window, &pw, &ph);
            PixelWidth = pw > 0 ? pw : width;
            PixelHeight = ph > 0 ? ph : height;
            _appliedScale = DisplayScale;
            Swapchain = GpuWindow.CreateSwapchain(Window, Owner.Backend, Owner.Device, PixelWidth, PixelHeight, Owner.IsOffscreen, Owner.Scheduler.FramesInFlight);

            // Its own layer (window stack, input bridge, surface, `ui` pass) on the owner layer's graphics factory, laid
            // out 1:1 in DIP (a dialog is not a design-space screen) with the owner's input settings.
            Ui = new UiLayer(this, host.Ui, registerFonts: null, designWidth: 0, designHeight: 0);
            Ui.Bridge.FocusFollowsMouse = host.Ui.Bridge.FocusFollowsMouse;
            Ui.Bridge.TabNavigation = host.Ui.Bridge.TabNavigation;
            Input.WindowWidth = PixelWidth;
            Input.WindowHeight = PixelHeight;
        }
        catch
        {
            Swapchain?.Dispose();
            SDL_DestroyWindow(Window);
            Window = null;
            throw;
        }
    }

    /// <summary>The window title (OS title bar).</summary>
    public string Title { get; }

    /// <summary>The SDL window (null once disposed).</summary>
    public SDL_Window* Window { get; private set; }

    /// <summary>The SDL window id (the owner's pump routes this window's events by it).</summary>
    public uint WindowId { get; }

    /// <summary>What <see cref="WindowIcon.Apply(SDL_Window*)"/> did for this window.</summary>
    public WindowIcon.Result Icon { get; private set; }

    /// <summary>True once SDL_SetWindowIcon succeeded on this window; false on SDL drivers without window icons.</summary>
    public bool HasIcon => Icon.Accepted;

    /// <summary>This window's swapchain, on the owner's device.</summary>
    public ISwapchain Swapchain { get; } = null!;

    /// <summary>This window's render graph (its `ui` pass and present; submitted on the owner's scheduler).</summary>
    public RenderGraph Graph { get; } = new();

    /// <summary>This window's MewUI layer (its own window stack, input bridge and surface).</summary>
    public UiLayer Ui { get; } = null!;

    /// <summary>This window's input, filled by the owner's pump.</summary>
    public RawInputSnapshot Input { get; } = new();

    /// <summary>The owner (main) window.</summary>
    public GpuWindow Owner { get; }

    /// <summary>The SDL id of the OS owner window (SDL_GetWindowParent), 0 when it has none.</summary>
    public uint OwnerWindowId
    {
        get
        {
            if (Window == null)
            {
                return 0;
            }

            SDL_Window* parent = SDL_GetWindowParent(Window);
            return parent == null ? 0 : (uint)SDL_GetWindowID(parent);
        }
    }

    /// <summary>
    /// True once the OS close button was pressed or <see cref="RequestClose"/> was called; the application closes the
    /// window (<see cref="AppHost.CloseSecondary"/>) at its frame boundary.
    /// </summary>
    public bool CloseRequested { get; private set; }

    /// <summary>True until the window is disposed.</summary>
    public bool IsOpen => !_disposed;

    /// <summary>Frames this window rendered and presented.</summary>
    public long FramesPresented { get; private set; }

    /// <summary>Backbuffer width, pixels.</summary>
    public int PixelWidth { get; private set; }

    /// <summary>Backbuffer height, pixels.</summary>
    public int PixelHeight { get; private set; }

    /// <summary>Window width, logical (OS) units: this window's mouse coordinates.</summary>
    public int LogicalWidth { get; private set; }

    /// <summary>Window height, logical (OS) units.</summary>
    public int LogicalHeight { get; private set; }

    /// <summary>The display's content scale (per-monitor DPI) of the monitor this window is on.</summary>
    public float DisplayScale { get; private set; } = 1f;

    /// <summary>
    /// The window's top-left corner in desktop coordinates (logical units): where it was created, then where the OS
    /// last reported it (SDL_EVENT_WINDOW_MOVED, e.g. at the end of a title-bar drag; routed by the owner's pump).
    /// </summary>
    public (int X, int Y) Position => (_x, _y);

    /// <summary>True while the window is an OS-modal child of its owner (<see cref="SetModal"/>).</summary>
    public bool IsModal { get; private set; }

    /// <summary>
    /// Called every frame before this window's present pass with its graph, backbuffer handle and texture (the journey
    /// harness's per-window readback). Null = none.
    /// </summary>
    public Action<RenderGraph, RgHandle, ITexture>? BeforePresent { get; set; }

    /// <summary>The color the backbuffer is cleared to when the UI did not draw.</summary>
    public ClearColor Background { get; set; } = new(0f, 0f, 0f, 1f);

    GpuBackend IUiSurface.Backend => Owner.Backend;

    IGraphicsDevice IUiSurface.Device => Owner.Device;

    int IUiSurface.FramesInFlight => Owner.Scheduler.FramesInFlight;

    /// <summary>Asks for this window to be closed (as its OS close button does).</summary>
    public void RequestClose()
    {
        CloseRequested = true;
    }

    /// <summary>Shows <paramref name="content"/> as this window's MewUI window (its popups open in this window).</summary>
    public void Show(Aprillz.MewUI.Window content)
    {
        ArgumentNullException.ThrowIfNull(content);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Ui.SetWindow(content);
        if (Ui.Host is null)
        {
            throw new InvalidOperationException("the UI layer of window '" + Title + "' failed to start (see the [ui] log line)");
        }

        content.Title = Title;
    }

    /// <summary>
    /// Moves the window's top-left corner to (<paramref name="x"/>, <paramref name="y"/>), desktop logical units. Throws
    /// when the video driver cannot place windows (SDL's offscreen driver).
    /// </summary>
    public void MoveTo(int x, int y)
    {
        if (Window == null)
        {
            return;
        }

        if (!SDL_SetWindowPosition(Window, x, y))
        {
            throw new InvalidOperationException("SDL_SetWindowPosition (" + Title + "): " + SDL_GetError());
        }

        OnMoved(x, y);
    }

    /// <summary>
    /// Makes the window modal on its owner (SDL_SetWindowModal: on Win32 the owner is disabled until the window closes,
    /// on X11 the window manager is told). False when the video driver cannot (SDL's offscreen driver has no window
    /// manager); the application's own input gate holds the block there.
    /// </summary>
    public bool SetModal(bool modal)
    {
        if (Window == null)
        {
            return false;
        }

        bool ok = SDL_SetWindowModal(Window, modal);
        IsModal = ok && modal;
        return ok;
    }

    /// <summary>Brings the window above its siblings and gives it the keyboard (an already open dialog asked for again).</summary>
    public void BringToFront()
    {
        if (Window != null)
        {
            SDL_RaiseWindow(Window);
        }
    }

    internal void OnCloseRequested()
    {
        CloseRequested = true;
    }

    internal void OnMoved(int x, int y)
    {
        _x = x;
        _y = y;
    }

    /// <summary>
    /// The input step of the owner's frame: metrics, this window's input into its UI, a resize when the window's pixel
    /// size or display scale changed (drains the queue first, as <see cref="GpuWindow.EnsureSize"/>).
    /// </summary>
    internal void BeginFrame(double dt)
    {
        if (_disposed)
        {
            return;
        }

        RefreshMetrics();
        Ui.Dispatch(Input, dt);
        int pw;
        int ph;
        SDL_GetWindowSizeInPixels(Window, &pw, &ph);
        bool sizeChanged = pw > 0 && ph > 0 && (pw != PixelWidth || ph != PixelHeight);
        bool dpiChanged = Math.Abs(DisplayScale - _appliedScale) > ScaleEpsilon;
        if (pw <= 0 || ph <= 0 || (!sizeChanged && !dpiChanged))
        {
            return;
        }

        Owner.WaitIdle();
        if (sizeChanged)
        {
            Swapchain.Resize((uint)pw, (uint)ph);
            PixelWidth = pw;
            PixelHeight = ph;
        }

        _appliedScale = DisplayScale;
        Ui.Resize();
    }

    /// <summary>
    /// The render step, inside the owner's frame <paramref name="ctx"/> after the owner's graph was submitted (see the
    /// file header): acquire, `ui` pass, readback hook, present pass, submit, present.
    /// </summary>
    internal void Render(FrameContext ctx)
    {
        if (_disposed)
        {
            return;
        }

        ITexture backbuffer = Swapchain.AcquireNextBackbuffer(out _);
        Graph.Reset();
        _bb = Graph.ImportTexture(
            new RgTextureDesc { Name = "secondary-backbuffer", Format = Swapchain.Format, Width = (uint)PixelWidth, Height = (uint)PixelHeight, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 },
            backbuffer.Handle,
            ResourceState.Common,
            isGraphOutput: true);
        if (Ui.Host is { } host)
        {
            host.MenuBackground = Background;
        }

        if (!Ui.Render(Graph, _bb, clearBackground: true))
        {
            Graph.AddPass("secondary-ui-fallback-clear", PassKind.Graphics, _clearSetup);
        }

        BeforePresent?.Invoke(Graph, _bb, backbuffer);
        Graph.AddPass("secondary-present", PassKind.Graphics, _presentSetup);
        Owner.Scheduler.Submit(Graph, ctx);
        Swapchain.Present(PresentSyncInterval);
        FramesPresented++;
        Input.ClearDeltas();
    }

    /// <summary>
    /// Frees the window's UI layer, swapchain and SDL window after draining the owner's queue (the GPU may still read
    /// its last frames). Idempotent. Call through <see cref="AppHost.CloseSecondary"/>.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsModal && Window != null)
        {
            SDL_SetWindowModal(Window, false);   // give the owner back before the window goes
            IsModal = false;
        }

        Owner.WaitIdle();
        Ui?.Dispose();
        Swapchain?.Dispose();
        if (Window != null)
        {
            SDL_DestroyWindow(Window);
            Window = null;
        }
    }

    // The window at its target position, created there natively (no create-then-move DPI bounce).
    private static SDL_Window* CreateWindowAt(string title, int x, int y, int w, int h, bool vulkanSurface, SDL_Window* owner)
    {
        SDL_PropertiesID props = SDL_CreateProperties();
        fixed (byte* n = SDL_PROP_WINDOW_CREATE_PARENT_POINTER)
        {
            SDL_SetPointerProperty(props, n, (nint)owner);
        }

        fixed (byte* n = SDL_PROP_WINDOW_CREATE_X_NUMBER)
        {
            SDL_SetNumberProperty(props, n, x);
        }

        fixed (byte* n = SDL_PROP_WINDOW_CREATE_Y_NUMBER)
        {
            SDL_SetNumberProperty(props, n, y);
        }

        fixed (byte* n = SDL_PROP_WINDOW_CREATE_WIDTH_NUMBER)
        {
            SDL_SetNumberProperty(props, n, w);
        }

        fixed (byte* n = SDL_PROP_WINDOW_CREATE_HEIGHT_NUMBER)
        {
            SDL_SetNumberProperty(props, n, h);
        }

        fixed (byte* n = SDL_PROP_WINDOW_CREATE_RESIZABLE_BOOLEAN)
        {
            SDL_SetBooleanProperty(props, n, true);
        }

        if (vulkanSurface)
        {
            fixed (byte* n = SDL_PROP_WINDOW_CREATE_VULKAN_BOOLEAN)
            {
                SDL_SetBooleanProperty(props, n, true);
            }
        }

        SDL_Window* win = SDL_CreateWindowWithProperties(props);
        SDL_DestroyProperties(props);
        if (win != null)
        {
            SDL_SetWindowTitle(win, title);
        }

        return win;
    }

    private void RefreshMetrics()
    {
        int lw;
        int lh;
        SDL_GetWindowSize(Window, &lw, &lh);
        if (lw > 0 && lh > 0)
        {
            LogicalWidth = lw;
            LogicalHeight = lh;
        }

        float s = Owner.IsOffscreen && GpuWindow.OffscreenDisplayScaleOverride is float forced ? forced : SDL_GetWindowDisplayScale(Window);
        DisplayScale = s > 0f ? s : 1f;
    }

    private void PresentSetup(PassBuilder s)
    {
        // As GpuWindow.PresentSetup: an offscreen swapchain never presents, so its backbuffer stays Common.
        s.Write(_bb, Owner.IsOffscreen ? ResourceState.Common : ResourceState.Present, ShaderStage.None);
        s.SetExecute(NoopExec);
    }

    private void ClearSetup(PassBuilder s)
    {
        s.Write(_bb, ResourceState.RenderTarget);
        s.SetExecute(_clearExec);
    }

    private void ClearExecute(in RgPassContext ctx)
    {
        ulong color = ctx.Resolve(_bb);
        Span<ColorAttachment> ca = [new ColorAttachment { TextureHandle = color, Clear = true, ClearValue = Background }];
        ctx.Recorder.BeginRendering(ca, default, (uint)PixelWidth, (uint)PixelHeight);
        ctx.Recorder.EndRendering();
    }
}
