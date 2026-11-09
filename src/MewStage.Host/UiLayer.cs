// The MewUI layer of a GpuWindow: ONE persistent EmbeddedUiHost that composites the whole visible window stack in a
// single `ui` render-graph pass, the SDL -> MewUI input bridge and the gamepad focus navigator. Input, focus and
// WantsInput target the TOP window only (modal). The host is created lazily on the first SetWindows, so a path that
// never shows MewUI pays nothing; a failed creation is reported once and not retried. A SecondaryWindow has its own
// layer whose host shares the owner layer's graphics factory (EmbeddedUiHost's shared-factory constructor).
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

using Aprillz.MewUI;

using MewStage.Graphics;
using MewStage.Input;
using MewStage.Rendering.Graph;
using MewStage.Ui.Backend;

namespace MewStage.Host;

/// <summary>The MewUI window stack on a <see cref="GpuWindow"/> (see the file header).</summary>
public sealed class UiLayer : IDisposable
{
    private readonly IUiSurface _gpu;
    private readonly UiLayer? _factoryOwner;
    private readonly Action<FontCollection>? _registerFonts;
    private readonly List<Window> _single = new(1);   // reusable backing list for SetWindow(Window?)
    private bool _triedInit;
    private bool _initFailed;

    /// <param name="gpu">The window, device and render graph the layer draws into.</param>
    /// <param name="registerFonts">Adds the app's faces to the backend (the built-in default is already there).</param>
    /// <param name="designWidth">With <paramref name="designHeight"/>: lay windows out in this fixed DIP design space
    /// and scale it to fit the window (a game HUD designed at 1920 x 1080); 0 = 1:1 DIP.</param>
    /// <param name="designHeight">See <paramref name="designWidth"/>.</param>
    public UiLayer(GpuWindow gpu, Action<FontCollection>? registerFonts, double designWidth, double designHeight)
        : this(gpu ?? throw new ArgumentNullException(nameof(gpu)), null, registerFonts, designWidth, designHeight)
    {
    }

    /// <summary>
    /// A layer on <paramref name="surface"/>; with <paramref name="factoryOwner"/> its host shares that layer's
    /// graphics factory (a second OS window of the same UI thread) and <paramref name="registerFonts"/> is unused (the
    /// owner registered the faces).
    /// </summary>
    internal UiLayer(IUiSurface surface, UiLayer? factoryOwner, Action<FontCollection>? registerFonts, double designWidth, double designHeight)
    {
        _gpu = surface ?? throw new ArgumentNullException(nameof(surface));
        _factoryOwner = factoryOwner;
        _registerFonts = registerFonts;
        DesignWidth = designWidth;
        DesignHeight = designHeight;
    }

    /// <summary>The design-space width in DIP, or 0 for 1:1 DIP layout.</summary>
    public double DesignWidth { get; private set; }

    /// <summary>The design-space height in DIP, or 0 for 1:1 DIP layout.</summary>
    public double DesignHeight { get; private set; }

    /// <summary>
    /// Switch the layout space: <paramref name="width"/> x <paramref name="height"/> DIP scaled to fit the window (a
    /// game HUD), or 0 x 0 for 1:1 DIP (a tool UI). A live host re-fits at once (its surface is rebuilt at the new
    /// render scale, so call this between frames with the device idle, as a screen does when it takes over); the same
    /// space again is a no-op.
    /// </summary>
    public void SetDesignSpace(double width, double height)
    {
        bool design = width > 0 && height > 0;
        if (!design)
        {
            width = 0;
            height = 0;
        }

        if (width == DesignWidth && height == DesignHeight)
        {
            return;
        }

        DesignWidth = width;
        DesignHeight = height;
        if (Host is { } host)
        {
            ApplyDesignSpace(host);
            host.Resize(_gpu.PixelWidth, _gpu.PixelHeight, _gpu.DisplayScale > 0 ? _gpu.DisplayScale : 1.0, force: true);
            host.Invalidate();
        }
    }

    /// <summary>The input bridge (pixel -> DIP scale set every frame by <see cref="Dispatch"/>).</summary>
    public SdlInputBridge Bridge { get; } = new();

    /// <summary>Spatial gamepad focus navigation of the top window.</summary>
    public GamepadFocusNavigator Focus { get; } = new();

    /// <summary>The embedded host (null until the first <see cref="SetWindows"/>, or when it failed to start).</summary>
    public EmbeddedUiHost? Host { get; private set; }

    /// <summary>The window input and focus go to (the top of the stack), or null. Follows the host: a window closed
    /// through <c>Window.Close()</c> leaves the stack on its own.</summary>
    public Window? TopWindow => Host?.TopWindow;

    /// <summary>True when the top window has a modal child open or a focused element is consuming input.</summary>
    public bool WantsInput => Host?.WantsInput ?? false;

    /// <summary>
    /// Create the host now instead of at the first <see cref="SetWindows"/>: an application that builds MewUI
    /// elements needing the graphics factory before it shows them (a chart measures text on attach) calls this once.
    /// Null when the host could not start (reported once on stderr).
    /// </summary>
    public EmbeddedUiHost? Start()
    {
        return EnsureHost();
    }

    /// <summary>Show just <paramref name="window"/> (or nothing).</summary>
    public void SetWindow(Window? window)
    {
        _single.Clear();
        if (window is not null)
        {
            _single.Add(window);
        }

        SetWindows(_single);
    }

    /// <summary>Show the z-ordered stack <paramref name="bottomToTop"/> (bottom first); empty = no UI pass.</summary>
    public void SetWindows(IReadOnlyList<Window> bottomToTop)
    {
        ArgumentNullException.ThrowIfNull(bottomToTop);
        EmbeddedUiHost? host = EnsureHost();
        if (host is null)
        {
            return;
        }

        Window? prevTop = host.TopWindow;
        host.SetWindows(bottomToTop);
        if (!ReferenceEquals(prevTop, host.TopWindow))
        {
            host.Invalidate();   // re-layout the newly-focused top
        }
    }

    /// <summary>Feed one frame of input to the top window (edge-diffed bridge + gamepad focus navigation).</summary>
    /// <param name="input">This frame's input snapshot.</param>
    /// <param name="dt">Seconds since the previous frame (drives the navigator's hold-to-repeat).</param>
    public void Dispatch(RawInputSnapshot input, double dt)
    {
        ArgumentNullException.ThrowIfNull(input);

        // In design space the pointer maps through the design fit, else through the display scale.
        Bridge.Scale = Host is { UseDesignSpace: true } ? (float)Host.EffectiveScale : _gpu.DisplayScale;
        Window? top = TopWindow;
        if (top is null || top.Handle == 0)
        {
            return;
        }

        Bridge.Dispatch(top, input);
        Focus.Update(top, input, dt);
    }

    /// <summary>Resize the UI surface and every tracked window (after <see cref="GpuWindow.EnsureSize"/>).</summary>
    public void Resize()
    {
        Host?.Resize(_gpu.PixelWidth, _gpu.PixelHeight, _gpu.DisplayScale > 0 ? _gpu.DisplayScale : 1.0);
    }

    /// <summary>
    /// Add the `ui` pass drawing the stack into <paramref name="target"/>; it clears the target first when
    /// <paramref name="clearBackground"/> (nothing drew under the UI this frame). True when a pass was added.
    /// </summary>
    public bool Render(RenderGraph graph, RgHandle target, bool clearBackground)
    {
        return Host is not null && Host.TopWindow is not null
            && Host.TickAndRender(graph, target, UiInvalidationMode.Continuous, clearBackground: clearBackground);
    }

    /// <summary>Free the layer's GPU resources (the device must be alive and idle: call <see cref="GpuWindow.WaitIdle"/> first).</summary>
    public void Dispose()
    {
        Focus.Dispose();
        Host?.Dispose();
        Host = null;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A UI host that fails to start (missing shaders, a device without the needed features) disables the UI for the run and is reported once; the application keeps rendering.")]
    private EmbeddedUiHost? EnsureHost()
    {
        if (Host is not null)
        {
            return Host;
        }

        if (_triedInit && _initFailed)
        {
            return null;   // don't spam a failed init
        }

        EmbeddedUiHost? host = null;
        try
        {
            (byte[] uv, byte[] up) = CookedShaders.Load(_gpu.Backend, "ui");

            // flipClipY: DX12 NDC is Y-up so the ui clip Y must flip (gFlipY = -1) to stay right-side-up; Vulkan NDC is
            // Y-down (no flip, gFlipY = +1, lavapipe-verified).
            bool flip = _gpu.Backend != GpuBackend.Vulkan;
            if (_factoryOwner is { } owner)
            {
                EmbeddedUiHost shared = owner.EnsureHost() ?? throw new InvalidOperationException("the owner window's UI layer has no host");
                host = new EmbeddedUiHost(_gpu.Device, shared.Factory, uv, up, flipClipY: flip, framesInFlight: _gpu.FramesInFlight);
            }
            else
            {
                host = new EmbeddedUiHost(_gpu.Device, uv, up, flipClipY: flip, framesInFlight: _gpu.FramesInFlight, registerFonts: _registerFonts);
            }

            ApplyDesignSpace(host);
            host.Resize(_gpu.PixelWidth, _gpu.PixelHeight, _gpu.DisplayScale > 0 ? _gpu.DisplayScale : 1.0);
            Host = host;
            _initFailed = false;
        }
        catch (Exception ex)
        {
            _initFailed = true;
            host?.Dispose();
            Console.Error.WriteLine("[ui] MewUI host init failed: " + ex.Message + " (UI disabled this run)");
            return null;
        }
        finally
        {
            _triedInit = true;
        }

        return Host;
    }

    private void ApplyDesignSpace(EmbeddedUiHost host)
    {
        bool design = DesignWidth > 0 && DesignHeight > 0;
        host.UseDesignSpace = design;
        host.DesignWidth = design ? DesignWidth : 0;
        host.DesignHeight = design ? DesignHeight : 0;
    }
}

/// <summary>
/// What a <see cref="UiLayer"/> draws onto: the device and backend, the frames in flight, the backbuffer size and the
/// display scale. Implemented by the main window (<see cref="GpuWindow"/>) and by every <see cref="SecondaryWindow"/>
/// (its own swapchain on the main window's device).
/// </summary>
internal interface IUiSurface
{
    GpuBackend Backend { get; }

    IGraphicsDevice Device { get; }

    int FramesInFlight { get; }

    int PixelWidth { get; }

    int PixelHeight { get; }

    float DisplayScale { get; }
}
