// EmbeddedUiHost: an embedded MewUI host that drives the visible MewUI Window STACK through the application's render
// graph instead of an OS message loop. The render graph runs ONE `ui` pass with ONE D24S8 stencil for the NanoVG
// stencil passes, so this host composites the WHOLE visible window stack (bottom -> top) into that SINGLE pass, on ONE
// surface, with ONE stencil cleared once at pass start:
//
//   * SetWindows(bottomToTop): the application hands the ordered visible windows each frame. Each window gets its own
//     EmbeddedWindowBackend (invalidation counter + non-zero Handle so PerformLayout runs) and is laid out in the shared
//     design space; backends for windows that left the stack are pruned (so an exited screen's Window is GC-able).
//     Input, gamepad focus and WantsInput target the TOP window ONLY (the modal trap).
//
//   * TickAndRender(graph, target, ...): once per frame. It (1) re-lays-out every visible window ONLY when something
//     invalidated (OnRequest) or an animation / transition is active (Continuous), and (2) wires the single `ui`
//     render-graph pass that opens ONE BeginRendering (color + a transient D24S8 stencil cleared once) and drives
//     Window.RenderFrameToPresentSurface(surface) for each window bottom -> top into ONE accumulated NanoVG frame
//     (surface.BeginWindowBatch / EndWindowBatch), flushed once. Command order across the windows is preserved, so an
//     opaque menu below composites correctly under a transparent dialog's scrim via premultiplied alpha; the shared
//     stencil is safe because the stencil passes self-reset and NanoVG's clip stack is reset per window's BeginFrame.
//
//   * The MewUI main host contract: the host PULSES the animation clocks once per frame (AnimationManager.UpdateAt;
//     MewUI no longer ticks animation inside render), keeps each window's graphics context across frames, and binds its
//     graphics factory as the thread's.
//
// A single window in the list is byte-identical to a single-window host: one BeginRendering, one flush.
//
// DevTools: in a Debug JIT build MewUI maps four shortcuts on every window. The two in-window tools (element
// inspector, performance monitor) work here; the two that open OS windows of their own (visual tree, profiler) need
// a platform host this process does not have and would throw, so their gestures are unmapped as a window attaches.

using System.Diagnostics;
using System.Runtime.CompilerServices;

using Aprillz.MewUI;
using Aprillz.MewUI.Animation;
using Aprillz.MewUI.Input;

using MewStage.Graphics;
using MewStage.Rendering.Graph;

namespace MewStage.Ui.Backend;

/// <summary>Window invalidation model: redraw only on request, or every frame while animating.</summary>
public enum UiInvalidationMode : byte
{
    /// <summary>Redraw only when a window invalidated or an animation needs frames.</summary>
    OnRequest = 0,

    /// <summary>Redraw every frame (a HUD over a live world, a viewport image).</summary>
    Continuous = 1,
}

/// <summary>Embedded MewUI host: composites the visible Window stack through ONE `ui` render-graph pass.</summary>
public sealed class EmbeddedUiHost : IDisposable
{
    private readonly IGraphicsDevice _device;

    // The visible windows this frame, bottom -> top (the last is the input/focus/modal target). Each has its OWN
    // backend (invalidation counter + Handle). One shared surface + stencil renders them all in one pass.
    private readonly List<Window> _windows = [];
    private readonly Dictionary<Window, EmbeddedWindowBackend> _backends = [];
    private readonly List<Window> _pruneScratch = [];   // reused: backends to drop this frame (no per-frame alloc)
    private readonly ConditionalWeakTable<Window, object> _closed = [];   // windows CloseWindow finished with (their trees are disposed)
    /// <summary>When true, lay windows out in a fixed <see cref="DesignWidth"/>×<see cref="DesignHeight"/> DIP
    /// design space and uniformly scale to the surface (letterbox far edges). Off = classic 1:1 DIP↔DPI.</summary>
    public bool UseDesignSpace { get; set; }
    /// <summary>Design-space width (DIP) when <see cref="UseDesignSpace"/>.</summary>
    public double DesignWidth { get; set; } = 1920;
    /// <summary>Design-space height (DIP) when <see cref="UseDesignSpace"/>.</summary>
    public double DesignHeight { get; set; } = 1080;
    /// <summary>Effective pixels-per-DIP of the last Resize (design-fit k, or the DPI scale). Feed to the input bridge.</summary>
    public double EffectiveScale { get; private set; } = 1.0;
    private readonly byte[] _uiVs, _uiPs;
    private readonly bool _flipClipY;
    private readonly int _framesInFlight;

    private UiRenderSurface? _surface;
    private int _w, _h;
    private double _dpiScale = 1.0;
    // Layout metrics computed by Resize + applied to every (existing + newly-added) window.
    private double _renderScale = 1.0, _dipW = 1, _dipH = 1;
    private bool _firstFrame = true;
    private bool _disposed;

    // ui-pass state (closure-free cached delegate; per-frame state in fields — GC rule).
    private readonly RgExecute _uiExec;
    private readonly Action<PassBuilder> _uiSetup;   // cached, capture-free ui-pass setup
    private RgHandle _uiColor, _uiDepth;
    // On a menu frame NO world pass clears the backbuffer, so the ui pass must clear its color target to
    // an opaque background (else any region the windows do not paint shows uninitialized backbuffer = a white
    // border). On an in-session frame the world already rendered, so the ui pass must LOAD (Clear=false) to
    // composite the HUD over the world. The application sets this per frame via TickAndRender(clearBackground:).
    private bool _clearBackground;

    /// <summary>Total re-draw requests across all visible windows since the last <see cref="ResetInvalidationCount"/>.</summary>
    public int InvalidationCount
    {
        get
        {
            int t = 0;
            foreach (EmbeddedWindowBackend b in _backends.Values)
            {
                t += b.InvalidationCount;
            }

            return t;
        }
    }
    /// <summary>How many times the host has actually re-laid-out + recorded the window stack.</summary>
    public int RenderCount { get; private set; }
    /// <summary>The TOP (input/focus/modal) window, or null when nothing is visible.</summary>
    public Window? TopWindow => _windows.Count > 0 ? _windows[^1] : null;
    /// <summary>Back-compat alias for the top window (the input/focus target).</summary>
    public Window? Window => TopWindow;
    /// <summary>True when the TOP window has a modal child open or a focused element is consuming input → gate gameplay input.</summary>
    public bool WantsInput
    {
        get
        {
            Window? top = TopWindow;
            if (top is null)
            {
                return false;
            }

            bool modalDisable = _backends.TryGetValue(top, out EmbeddedWindowBackend? b) && b.HasModalDisable;
            return top.GetTopModalChild() != null || modalDisable;
        }
    }

    /// <summary>Primary ctor: no window bound yet — call <see cref="SetWindows"/> each frame with the visible stack.
    /// <paramref name="registerFonts"/> adds the app's font faces to the factory (the built-in default is there).</summary>
    public EmbeddedUiHost(IGraphicsDevice device, byte[] uiVs, byte[] uiPs, bool flipClipY = false, int framesInFlight = 2, Action<FontCollection>? registerFonts = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _uiVs = uiVs ?? throw new ArgumentNullException(nameof(uiVs));
        _uiPs = uiPs ?? throw new ArgumentNullException(nameof(uiPs));
        _flipClipY = flipClipY;
        _framesInFlight = framesInFlight < 1 ? 1 : framesInFlight;
        Factory = UiGraphicsFactory.Register(device, uiVs, uiPs, flipClipY, _framesInFlight);
        registerFonts?.Invoke(Factory.Fonts);
        BindThread();
        ApplyEmbeddedHostDefaults();
        _uiExec = UiExecute;
        _uiSetup = UiSetup;
        _retainedClock = RetainedNow;
        _ownsFactory = true;
    }

    /// <summary>A host on <paramref name="sharedFactory"/> (owned by another host on the SAME UI thread and device): the
    /// layer of a second OS window (a SecondaryWindow in MewStage.Host). It has its own surface, glyph atlas,
    /// window stack and `ui` pass; the factory - fonts, interned fonts, the text engine MewUI measures with - is the
    /// owner's, so the thread's graphics factory (MewUI patch 0015) is the same whichever host bound it last, and this
    /// host never disposes it.</summary>
    public EmbeddedUiHost(IGraphicsDevice device, UiGraphicsFactory sharedFactory, byte[] uiVs, byte[] uiPs, bool flipClipY = false, int framesInFlight = 2)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        Factory = sharedFactory ?? throw new ArgumentNullException(nameof(sharedFactory));
        _uiVs = uiVs ?? throw new ArgumentNullException(nameof(uiVs));
        _uiPs = uiPs ?? throw new ArgumentNullException(nameof(uiPs));
        _flipClipY = flipClipY;
        _framesInFlight = framesInFlight < 1 ? 1 : framesInFlight;
        BindThread();
        ApplyEmbeddedHostDefaults();
        _uiExec = UiExecute;
        _uiSetup = UiSetup;
        _retainedClock = RetainedNow;
        _ownsFactory = false;
    }

    // False for a host on another host's factory (the shared-factory constructor): Dispose leaves it alone.
    private readonly bool _ownsFactory;

    // ---- the retained renderer's pass clock ----
    // MewUI's retained renderer draws a visual LIVE (stops recording it) once its content changed in 30 passes in a
    // row, each within VisualNode.ANIMATION_GAP_MS (50 ms) of the last (the gap tells an animation from a caret that
    // changes in every pass of an otherwise idle OnRequest UI, half a second apart). In Continuous mode every
    // TickAndRender IS a pass, so pass N-1 -> N is consecutive by construction; on the wall clock, though, one stall
    // over 50 ms (thread preempted on a loaded machine, a GC pause) split the run and the viewport image (it changes
    // every frame) went back to being RECORDED for 30 passes: 2360 B, then 120 B per frame (measured as allocation-gate
    // failures). Continuous passes are therefore stamped at most one ContinuousPassStep
    // apart; OnRequest passes keep the wall clock (their gap is the signal).
    /// <summary>Largest step between the stamps of two Continuous passes: half MewUI's 50 ms animation gap
    /// (VisualNode.ANIMATION_GAP_MS), so consecutive Continuous passes always continue a run.</summary>
    public static readonly long ContinuousPassStepTicks = Stopwatch.Frequency * 25 / 1000;
    private static readonly Func<long> WallClock = Stopwatch.GetTimestamp;
    private readonly Func<long> _retainedClock;
    private long _retainedNow;
    private long RetainedNow()
    {
        return _retainedNow == 0 ? Stopwatch.GetTimestamp() : _retainedNow;
    }

    private void StampPass(UiInvalidationMode mode)
    {
        long wall = Stopwatch.GetTimestamp();
        _retainedNow = mode == UiInvalidationMode.Continuous && _retainedNow != 0
            ? Math.Min(wall, _retainedNow + ContinuousPassStepTicks)
            : wall;
    }

    /// <summary>Back-compat convenience ctor: host a single <paramref name="window"/> (seeds the stack with it).</summary>
    public EmbeddedUiHost(IGraphicsDevice device, Window window, byte[] uiVs, byte[] uiPs, bool flipClipY = false, int framesInFlight = 2, Action<FontCollection>? registerFonts = null)
        : this(device, uiVs, uiPs, flipClipY, framesInFlight, registerFonts)
    {
        ArgumentNullException.ThrowIfNull(window);
        _singleSeed[0] = window;
        SetWindows(_singleSeed);
    }
    private readonly Window[] _singleSeed = new Window[1];

    /// <summary>The graphics factory (fonts, the text engine's measurement) this host registered as MewUI's default.</summary>
    public UiGraphicsFactory Factory { get; }

    /// <summary>The font faces the factory resolves fonts from.</summary>
    public FontCollection Fonts => Factory.Fonts;

    /// <summary>Process-wide MewUI switches for a host with no platform behind it, set by every host:
    /// <list type="bullet">
    /// <item>MewUI opens a native OS window for a popup (ComboBox list, tooltip), a dialog, or a drag preview unless
    /// told otherwise; an embedded host has no OS windows, so they open inside the window's own layers (the Browser
    /// platform does the same).</item>
    /// <item>The theme variant is named. Left at System, every element asks MewUI's platform host for the OS theme the
    /// first time it is styled; there is none, so each ask threw and fell back to Light - one caught exception per
    /// element of every screen built. Light is that fallback, so nothing changes but the exceptions.</item>
    /// </list></summary>
    internal static void ApplyEmbeddedHostDefaults()
    {
        PopupManager.PreferNativePopups = false;
        Window.PreferNativeDialogWindows = false;
        WindowDragDropRouter.PreferNativePreviewWindow = false;
        // Only a variant nobody named is resolved here: a host created AFTER an app installed its theme (the editor's
        // sets Dark; the layer of every secondary OS window constructs another host) must not flip the
        // process default back to Light, or every element built afterwards (a lazily built workspace, a window's tooltip)
        // would be styled Light.
        if (!Application.IsRunning && ThemeManager.Default == ThemeVariant.System)
        {
            ThemeManager.Default = ThemeVariant.Light;
        }
    }

    /// <summary>How many NanoVG draw calls the last `ui` pass recorded (0 before the first frame): a statistics readout.</summary>
    public int LastFrameDrawCalls => _surface?.Vg.Renderer.LastFlushCallCount ?? 0;

    /// <summary>Set the visible window stack (bottom -> top). Attaches a backend to any new window, applies the
    /// current layout metrics, and prunes backends for windows that left the stack (so their Window can be GC'd).
    /// Reuses the backing collections — allocation-free in steady state (same stack each frame).</summary>
    public void SetWindows(IReadOnlyList<Window> bottomToTop)
    {
        ArgumentNullException.ThrowIfNull(bottomToTop);
        BindThread();

        _windows.Clear();
        for (int i = 0; i < bottomToTop.Count; i++)
        {
            Window? w = bottomToTop[i];
            if (w is null)
            {
                continue;
            }

            _windows.Add(w);
            if (!_backends.TryGetValue(w, out _))
            {
                if (_closed.TryGetValue(w, out _))
                {
                    throw new InvalidOperationException("a closed window (Window.Close / EmbeddedUiHost.CloseWindow disposed its visual tree) cannot be shown again");
                }

                var backend = new EmbeddedWindowBackend(this, w);
                w.AttachBackend(backend);
                UnmapWindowOpeningDevTools(w);
                w.RetainedClock = _retainedClock;   // this host's pass clock (see StampPass)
                _backends[w] = backend;
                ApplyWindowMetrics(w, backend);   // size/DPI the new window immediately (before its first layout)
                if (_surface is not null)
                {
                    w.RaiseLoaded();   // attached + sized: Loaded handlers see an arranged tree
                }

                backend.RaiseInvalidate();         // a freshly-added window needs an initial layout + paint
            }
        }

        // Prune backends whose window left the stack (drops our reference so an exited screen's Window is GC-able),
        // releasing the context the window kept for our surface.
        _pruneScratch.Clear();
        foreach (KeyValuePair<Window, EmbeddedWindowBackend> kv in _backends)
        {
            if (!_windows.Contains(kv.Key))
            {
                _pruneScratch.Add(kv.Key);
            }
        }

        for (int i = 0; i < _pruneScratch.Count; i++)
        {
            PruneBackend(_pruneScratch[i]);
        }

        ActivateTop();
    }

    /// <summary>
    /// Close <paramref name="window"/> the way an OS backend's destroy would, synchronously: <c>Closing</c> is asked
    /// (a handler can cancel, or defer and finish later by closing again), <c>Closed</c> is raised, the window leaves
    /// the stack and the one under it becomes active, its graphics are released, and its visual tree is disposed, so
    /// every element drops what it held (an Image's rasterized vector returns to the scratch pool, a BitmapCache too).
    /// A closed window cannot be shown again; build a new one. <c>Window.Close()</c> routes here for any window this
    /// host attached, in the stack or not (a pause menu popped earlier and closed on the way out of the screen).
    /// Returns false when the window is not in the stack or a <c>Closing</c> handler kept it open.
    /// </summary>
    public bool CloseWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return _backends.TryGetValue(window, out EmbeddedWindowBackend? backend) && CloseWindow(window, backend);
    }

    /// <summary>The close itself, for a window in the stack or one pruned from it that still carries our backend.</summary>
    internal bool CloseWindow(Window window, EmbeddedWindowBackend backend)
    {
        BindThread();
        if (backend.IsClosed)
        {
            return false;
        }

        if (!window.RequestClose())
        {
            return false;   // cancelled, or a deferral is pending: its completion closes again
        }

        nint handle = backend.Handle;
        window.RaiseClosed();
        window.ClearMouseOverState();
        window.ClearMouseCaptureState();
        bool wasShown = _windows.Remove(window);
        if (_backends.ContainsKey(window))
        {
            PruneBackend(window);
        }

        window.ReleaseWindowGraphicsResources(handle);
        window.DisposeVisualTree();
        backend.MarkClosed();
        _closed.AddOrUpdate(window, ClosedMarker);
        if (wasShown)
        {
            ActivateTop();
            Invalidate();
        }

        return true;
    }

    private static readonly object ClosedMarker = new();

    private void PruneBackend(Window gone)
    {
        gone.ReleasePresentSurfaceContext();
        gone.SetIsActive(false);
        gone.RetainedClock = WallClock;   // back to MewUI's own clock for whoever hosts it next
        _ = _backends.Remove(gone);
    }

    /// <summary>Only the top window is active (input, focus visuals); the ones under it are not.</summary>
    private void ActivateTop()
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            _windows[i].SetIsActive(i == _windows.Count - 1);
        }
    }

    /// <summary>Remove the gestures of the two DevTools that open OS windows of their own (see the file header): there is
    /// no platform host here to open them on. A no-op in builds without the DevTools.</summary>
    public static void UnmapWindowOpeningDevTools(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!WindowDevTools.IsSupported)
        {
            return;
        }

        window.InputMap.Unmap(new KeyGesture(Key.T, ModifierKeys.Primary | ModifierKeys.Shift));
        window.InputMap.Unmap(new KeyGesture(Key.P, ModifierKeys.Primary | ModifierKeys.Shift | ModifierKeys.Alt));
    }

    /// <summary>Resize the UI surface (call when the window/backbuffer pixel size changes) and re-apply layout
    /// metrics to every tracked window. An unchanged size is a no-op unless <paramref name="force"/> (the layout
    /// space changed: <see cref="UseDesignSpace"/> / <see cref="DesignWidth"/> / <see cref="DesignHeight"/>), which
    /// rebuilds the surface at the new render scale; the device must be idle for that, as for any resize.</summary>
    public void Resize(int pixelWidth, int pixelHeight, double dpiScale = 1.0, bool force = false)
    {
        BindThread();
        pixelWidth = Math.Max(1, pixelWidth);
        pixelHeight = Math.Max(1, pixelHeight);
        if (!force && pixelWidth == _w && pixelHeight == _h && Math.Abs(dpiScale - _dpiScale) < 1e-9 && _surface is not null)
        {
            return;
        }

        _w = pixelWidth;
        _h = pixelHeight;
        _dpiScale = dpiScale <= 0 ? 1.0 : dpiScale;

        // Effective render scale + window DIP size. Design-space mode lays windows out at a FIXED
        // DesignWidth×DesignHeight DIP (absolute 1080p coords + exact hit-testing) and uniformly fits the whole
        // frame to the surface: k = min(px/designW, py/designH), letterbox on the far edges (the ui pass clears
        // the backbuffer). Otherwise the classic 1:1 DIP↔DPI path (dip = px/dpiScale).
        double renderScale, dipW, dipH;
        if (UseDesignSpace && DesignWidth > 0 && DesignHeight > 0)
        {
            renderScale = Math.Min(_w / DesignWidth, _h / DesignHeight);
            if (renderScale <= 0)
            {
                renderScale = 1.0;
            }

            dipW = DesignWidth;
            dipH = DesignHeight;
        }
        else
        {
            renderScale = _dpiScale;
            dipW = _w / _dpiScale;
            dipH = _h / _dpiScale;
        }

        EffectiveScale = renderScale;
        _renderScale = renderScale;
        _dipW = dipW;
        _dipH = dipH;

        // Contexts kept for the old surface point at it: release them before it goes.
        foreach (Window w in _backends.Keys)
        {
            w.ReleasePresentSurfaceContext();
        }

        _surface?.Dispose();
        _surface = new UiRenderSurface(_device, _w, _h, renderScale, _uiVs, _uiPs, _flipClipY, _framesInFlight);

        // Re-apply metrics to every tracked window, restart its sizing transaction, and force a re-layout/redraw.
        foreach (KeyValuePair<Window, EmbeddedWindowBackend> kv in _backends)
        {
            ApplyWindowMetrics(kv.Key, kv.Value);
            kv.Key.InvalidateSizingTransaction();
            kv.Key.RaiseLoaded();   // no-op once raised; a window attached before the first Resize gets it here
            kv.Value.RaiseInvalidate();
        }
    }

    // Size + DPI one window onto the current surface grid. SetDpi keeps layout snapping + the DIP→pixel render
    // transform on the same grid as the surface render scale (else geometry snapping rounds on the wrong pixel
    // grid and content falls ~1 device-px short at the edges).
    private void ApplyWindowMetrics(Window window, EmbeddedWindowBackend backend)
    {
        if (_surface is null)
        {
            return;
        }

        backend.SetClientSizePx(_w, _h, _renderScale);
        window.SetDpi((uint)Math.Round(96.0 * _renderScale));
        window.SetClientSizeDip(_dipW, _dipH);
    }

    /// <summary>The end of one host turn, which is what MewUI's dispatcher does after it drains (DispatcherQueue
    /// .NotifyDrainCompleted): re-query CanExecute for every command-bound control of every window, so a button bound
    /// to a command enables as soon as its screen registers the handler and follows the handler's CanExecute after
    /// that. No ApplicationDispatcher is installed in the embedded host (invalidation routes directly via the
    /// backend), so this is that pass. It visits only the registered command sources and allocates nothing. Throws if
    /// called after Dispose.</summary>
    public void PumpDispatcher()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (int i = 0; i < _windows.Count; i++)
        {
            _windows[i].EvaluateCommandStates();
        }
    }

    /// <summary>Re-query CanExecute for <paramref name="window"/>'s command-bound controls now (the pass
    /// <see cref="PumpDispatcher"/> runs every turn): for a screen that has just registered its command handlers, so
    /// its buttons are enabled before its first layout and focus.</summary>
    public static void RefreshCommandStates(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.EvaluateCommandStates();
    }

    /// <summary>Advance MewUI's animation clocks (transitions, animated controls) to <paramref name="timestamp"/>
    /// (Stopwatch ticks). MewUI main no longer ticks animation inside render; the host pulses once per frame.
    /// Tests pass a fixed timestamp to step a transition deterministically.</summary>
    public void PulseAnimations(long timestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AnimationManager.Instance.UpdateAt(timestamp);
    }

    /// <summary>True when a running animation of THIS thread's UI needs frames (the host renders while it does).
    /// The multi-host vendor patch: animation clocks are per UI thread, so a host pulses only its own thread's transitions.</summary>
    public static bool AnimationsNeedFrames => AnimationManager.Instance.HasRenderDemand;

    /// <summary>True when the host should re-record this frame (OnRequest: any window invalidated or an animation is
    /// running; Continuous: always).</summary>
    public bool ShouldRender(UiInvalidationMode mode)
    {
        if (_firstFrame || mode == UiInvalidationMode.Continuous)
        {
            return true;
        }

        if (AnimationsNeedFrames)
        {
            return true;
        }

        foreach (EmbeddedWindowBackend b in _backends.Values)
        {
            if (b.InvalidationCount > 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Per-frame entry point: lays out every visible window (only when needed) and wires the single `ui`
    /// render-graph pass that composites the stack onto <paramref name="graph"/>. Insert it after the world passes and
    /// before the present pass. <paramref name="backbufferHandle"/> is the render target the UI composites into.
    /// Returns true if the pass was wired (a redraw happened this frame).
    /// </summary>
    public bool TickAndRender(RenderGraph graph, ulong backbufferHandle, UiInvalidationMode mode = UiInvalidationMode.OnRequest, bool clearBackground = false, long? animationTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (_surface is null)
        {
            throw new InvalidOperationException("Call Resize(...) before TickAndRender.");
        }

        BindThread();
        PumpDispatcher();
        PulseAnimations(animationTimestamp ?? Stopwatch.GetTimestamp());
        if (!ShouldRender(mode))
        {
            _sampled.Clear();
            return false;
        }

        _clearBackground = clearBackground;
        StampPass(mode);

        LayoutAll();

        // Standalone path (caller hands a raw backbuffer handle, e.g. headless tests): import it ourselves.
        RgHandle graphSurface = graph.ImportTexture(
            new RgTextureDesc { Name = "ui-color", Format = Format.B8G8R8A8_UNorm, Width = (uint)_w, Height = (uint)_h, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 },
            backbufferHandle, ResourceState.RenderTarget, isGraphOutput: false);
        WireUiPass(graph, graphSurface);
        FinishFrame();
        return true;
    }

    /// <summary>
    /// Composite into an ALREADY-IMPORTED render-graph target (the host's shared backbuffer handle). Use this from
    /// a host that owns the backbuffer import so the world, ui and present passes all reference ONE logical resource
    /// (correct barriers, no duplicate import). Insert it after the world passes and before the present pass.
    /// </summary>
    public bool TickAndRender(RenderGraph graph, RgHandle target, UiInvalidationMode mode = UiInvalidationMode.OnRequest, bool clearBackground = false, long? animationTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (_surface is null)
        {
            throw new InvalidOperationException("Call Resize(...) before TickAndRender.");
        }

        BindThread();
        PumpDispatcher();
        PulseAnimations(animationTimestamp ?? Stopwatch.GetTimestamp());
        if (!ShouldRender(mode))
        {
            _sampled.Clear();
            return false;
        }

        _clearBackground = clearBackground;
        StampPass(mode);

        LayoutAll();
        WireUiPass(graph, target);
        FinishFrame();
        return true;
    }

    // Lay out every visible window (each self-skips when nothing is dirty + size unchanged — see Window.PerformLayout).
    private void LayoutAll()
    {
        for (int i = 0; i < _windows.Count; i++)
        {
            Window w = _windows[i];
            w.PerformLayout();
        }
    }

    private void WireUiPass(RenderGraph graph, RgHandle target)
    {
        _uiColor = target;   // per-frame target lives in a field; UiSetup reads it (no closure capture)
        graph.AddPass("ui", PassKind.Graphics, _uiSetup);
    }

    // Textures the windows SAMPLE this frame (3D viewport targets drawn as images). The ui pass declares
    // each as a pixel-shader read, so the graph transitions it from the producer's write to ShaderResource before the
    // ui pass. Cleared after every ui pass is added (a frame's list never leaks into the next frame's graph).
    private readonly List<RgHandle> _sampled = [];

    /// <summary>Declare that this frame's `ui` pass samples <paramref name="texture"/> (a viewport target written by an
    /// earlier pass of the same graph). Call before <c>TickAndRender</c>, every frame the texture is drawn.</summary>
    public void AddSampledTexture(RgHandle texture)
    {
        _sampled.Add(texture);
    }

    /// <summary>Drops the declared sampled textures (a frame that adds no ui pass after declaring them).</summary>
    public void ClearSampledTextures()
    {
        _sampled.Clear();
    }

    /// <summary>Number of sampled textures declared for the next ui pass (tests).</summary>
    public int PendingSampledTextureCount => _sampled.Count;

    private void UiSetup(PassBuilder s)
    {
        s.Write(_uiColor, ResourceState.RenderTarget);
        for (int i = 0; i < _sampled.Count; i++)
        {
            s.Read(_sampled[i], ShaderStage.Pixel, ResourceState.ShaderResource);
        }

        _sampled.Clear();
        // ONE `ui` pass means ONE transient D24S8 stencil. A lone transient gets its own physical slot (aliased or
        // not), so no dedicated-backing workaround is needed, and the compiler owns all barriers. The stencil is
        // cleared to 0 once at pass start (UiExecute); NanoVG stencil-then-cover self-resets per shape, so the whole
        // window stack shares this one stencil cleanly.
        _uiDepth = s.CreateDepth(new RgTextureDesc { Name = "ui-stencil", Format = Format.D24_UNorm_S8_UInt, Width = (uint)_w, Height = (uint)_h, SampleCount = 1, MipLevels = 1, ArrayLayers = 1 });
        s.SetExecute(_uiExec);
    }

    private void FinishFrame()
    {
        foreach (EmbeddedWindowBackend b in _backends.Values)
        {
            b.ResetInvalidationCount();
        }

        _firstFrame = false;
        RenderCount++;
    }

    private void UiExecute(in RgPassContext ctx)
    {
        if (_surface is null)
        {
            return;
        }

        ulong color = ctx.Resolve(_uiColor);
        ulong depth = ctx.Resolve(_uiDepth);
        IGraphicsRecorder rec = ctx.Recorder;

        Span<ColorAttachment> ca =
        [
            // Menu frame (no world rendered) -> clear to an opaque background so the whole backbuffer is covered (no
            // white/uninitialized border). In-session frame -> load (Clear=false) so the HUD composites over the world.
            _clearBackground
                ? new ColorAttachment { TextureHandle = color, Clear = true, ClearValue = MenuBackground }
                : new ColorAttachment { TextureHandle = color, Clear = false },
        ];
        var da = new DepthAttachment { TextureHandle = depth, Clear = true, ClearDepth = 0f, HasStencil = true, ClearStencilOnLoad = true, ClearStencil = 0 };

        // Composite the visible stack bottom -> top into ONE accumulated NanoVG frame (each window's own
        // BeginFrame/EndFrame cycle appends into the shared staging), flushed ONCE by EndWindowBatch. Command
        // order = z-order: an opaque bottom window fills its own background; transparent upper windows (dialog
        // scrim) composite over it via premultiplied alpha. The one stencil is cleared once (BeginRendering).
        // The windows' pass is CPU-only (MewVG appends draws and QUEUES texture uploads); nothing reaches the GPU
        // until the uploads are recorded below and the batch is flushed inside the render pass.
        // Scratch renders (a vector image's bitmap, an element's BitmapCache) happen from OnRender during the
        // traversal below and record straight onto this frame (their own render passes, ahead of ours), so the
        // device knows the frame from here to the end of the pass.
        Factory.Device.BeginFrameRecording(rec, ctx.CommandList);
        try
        {
            _surface.BeginGraphRecording(rec);
            _surface.BeginWindowBatch();
            for (int i = 0; i < _windows.Count; i++)
            {
                _windows[i].RenderFrameToPresentSurface(_surface);   // kept context -> MewVG append
            }
            // Glyph-atlas / mask uploads on the frame's OWN command list, before the render pass (copies are not allowed
            // inside one).
            _surface.RecordPendingUploads(ctx.CommandList);

            rec.BeginRendering(ca, da, (uint)_w, (uint)_h);
            rec.SetViewportScissor(0, 0, _w, _h);
            _surface.EndWindowBatch();                        // ONE flush: record all windows' draws onto rec
            rec.EndRendering();
        }
        finally
        {
            LastFrameScratchRenders = Factory.Device.ScratchRendersThisFrame;
            Factory.Device.EndFrameRecording();
        }
    }

    /// <summary>Scratch renders (vector-image and bitmap-cache rasterizations) the last `ui` pass recorded ahead of
    /// itself. 0 on a frame that only blitted its caches.</summary>
    public int LastFrameScratchRenders { get; private set; }

    /// <summary>Scratch surfaces alive on this host's device (cached vector images, bitmap caches, pooled spares);
    /// each realized one is a registered external texture besides the application's own.</summary>
    public int LiveScratchSurfaces => Factory.Device.LiveScratchSurfaces;

    /// <summary>Scratch renders since this host's device was created: steady while the UI only blits its caches, up by
    /// one per cache that is filled again (a new source, a new pixel size, a bitmap-cached subtree that changed).</summary>
    public int TotalScratchRenders => Factory.Device.TotalScratchRenders;

    /// <summary>Release the UI caches no element leases (the scratch pool that closed windows returned surfaces to,
    /// idle cached resources). The pool keeps surfaces across screens on purpose, so a re-entered screen reuses them;
    /// call this between frames on a transition where memory matters more (leaving an editor screen for the game).
    /// Caches still leased by a shown window stay.</summary>
    public void TrimCaches()
    {
        BindThread();
        Factory.Device.TrimCaches();
    }

    /// <summary>The opaque background the ui pass clears to on a menu frame (clearBackground:true). Ignored when
    /// compositing over a world (clearBackground:false). Default: a dark slate matching the world clear tone.</summary>
    public ClearColor MenuBackground { get; set; } = new(0.08f, 0.09f, 0.11f, 1f);

    /// <summary>Reset every visible window's invalidation counter.</summary>
    public void ResetInvalidationCount()
    {
        foreach (EmbeddedWindowBackend b in _backends.Values)
        {
            b.ResetInvalidationCount();
        }
    }
    /// <summary>Force every visible window to be re-laid-out + redrawn on the next TickAndRender (e.g. external content swap).</summary>
    public void Invalidate()
    {
        foreach (EmbeddedWindowBackend b in _backends.Values)
        {
            b.RaiseInvalidate();
        }
    }

    /// <summary>Re-assert this host's graphics factory as MewUI's process-wide default
    /// (Application.DefaultGraphicsFactory). Idempotent; no-op after Dispose.</summary>
    public void EnsureFactoryDefault()
    {
        if (_disposed)
        {
            return;
        }

        Application.DefaultGraphicsFactory = Factory;
        BindThread();
    }

    // The multi-host vendor patch: this host's factory is THE factory of the thread it runs on (Application
    // ThreadGraphicsFactory), re-bound at every TickAndRender, so its elements never measure or draw with another
    // host's factory - a host created later in the process (another test, a second window host) or one running on
    // another thread re-pointed the process-wide default and shared MewUI's per-factory text engine across threads
    // ("An item with the same key has already been added").
    private void BindThread()
    {
        if (!_disposed)
        {
            Application.ThreadGraphicsFactory = Factory;
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
        // A host on a shared factory leaves the thread's factory and the factory itself to their owner (still running).
        if (_ownsFactory && ReferenceEquals(Application.ThreadGraphicsFactory, Factory))
        {
            Application.ThreadGraphicsFactory = null;
        }

        foreach (Window w in _backends.Keys)
        {
            w.ReleasePresentSurfaceContext();
        }

        _surface?.Dispose();
        if (_ownsFactory)
        {
            Factory.Dispose();
        }
    }
}
