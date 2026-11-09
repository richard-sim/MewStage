// The viewport bridge's frame driver. Per frame, for every attached viewport that is visible in a laid-out window:
//   1. size its offscreen target to the control's arranged rect in device pixels (allocate / re-allocate);
//   2. add the world passes (one IViewportSceneRenderer per viewport) into that target;
//   3. optionally add a readback copy (tests, screenshots);
//   4. declare the target as a texture the `ui` pass samples (EmbeddedUiHost.AddSampledTexture, which makes the graph
//      compiler emit the RenderTarget -> ShaderResource barrier before the pass) and invalidate the control.
// After the `ui` pass, a terminal pass returns each target to Common: the state the renderer's next-frame import
// declares (like a swapchain backbuffer after Present).
// Sizing uses the layout of the previous UI frame (layout runs inside the ui pass's TickAndRender), so a resize
// shows the old target stretched for one frame and the new one from the next frame on.
// Closure-free per frame: cached delegates read per-frame fields.
using System;
using System.Collections.Generic;

using MewStage.Graphics;
using MewStage.Rendering.Graph;
using MewStage.Ui.Backend;

namespace MewStage.Viewport;

/// <summary>Drives the offscreen rendering of every attached <see cref="ViewportControl"/> (see the file header).</summary>
public sealed class ViewportCompositor : IDisposable
{
    private static readonly RgExecute NoOp = static (in _) => { };

    private readonly IGraphicsDevice _device;
    private readonly EmbeddedUiHost _ui;
    private readonly bool _tightReadbackRows;
    private readonly int _framesInFlight;
    private readonly List<Slot> _slots = [];
    private readonly Action<PassBuilder> _terminalSetup;
    private RgHandle _terminalTarget;
    private bool _disposed;

    /// <param name="device">The GPU device.</param>
    /// <param name="ui">The UI host whose `ui` pass samples the viewport targets.</param>
    /// <param name="tightReadbackRows">True on Vulkan / headless, false on DX12 (see <see cref="TextureReadback"/>).</param>
    /// <param name="framesInFlight">The scheduler's frame depth.</param>
    public ViewportCompositor(IGraphicsDevice device, EmbeddedUiHost ui, bool tightReadbackRows, int framesInFlight)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _tightReadbackRows = tightReadbackRows;
        _framesInFlight = Math.Max(1, framesInFlight);
        _terminalSetup = TerminalSetup;
    }

    /// <summary>Number of attached viewports.</summary>
    public int Count => _slots.Count;

    /// <summary>Number of viewports rendered in the last <see cref="AddViewportPasses"/>.</summary>
    public int RenderedLastFrame { get; private set; }

    /// <summary>Attaches a viewport with its own world renderer. The compositor owns neither the control nor the renderer; it owns the viewport's target.</summary>
    public void Attach(ViewportControl control, IViewportSceneRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(renderer);
        if (Find(control) >= 0)
        {
            throw new InvalidOperationException("viewport is already attached");
        }

        _slots.Add(new Slot(control, renderer, new ViewportTarget(_device, renderer.ColorFormat)));
    }

    /// <summary>Detaches a viewport and frees its target (deferred past the frames in flight).</summary>
    public void Detach(ViewportControl control)
    {
        int i = Find(control);
        if (i < 0)
        {
            return;
        }

        Slot s = _slots[i];
        s.Control.Image = null;
        s.Target.Dispose();
        s.ColorReadback?.Dispose();
        _slots.RemoveAt(i);
    }

    /// <summary>True when <paramref name="control"/> is attached.</summary>
    public bool IsAttached(ViewportControl control)
    {
        return Find(control) >= 0;
    }

    /// <summary>Requests color readback of a viewport every frame (tests, screenshots); results via <see cref="GetColorReadback"/>.</summary>
    public void EnableColorReadback(ViewportControl control)
    {
        Slot s = _slots[Require(control)];
        s.ColorReadback ??= new TextureReadback(_device, sizeof(uint), _tightReadbackRows, _framesInFlight);
    }

    /// <summary>The color readback of a viewport, or null when not enabled.</summary>
    public TextureReadback? GetColorReadback(ViewportControl control)
    {
        return _slots[Require(control)].ColorReadback;
    }

    /// <summary>The target of a viewport (tests: size and generation).</summary>
    public ViewportTarget GetTarget(ViewportControl control)
    {
        return _slots[Require(control)].Target;
    }

    /// <summary>Adds every visible viewport's world (+ readback) passes. Call after the backbuffer import and BEFORE the `ui` pass (<c>AppHost.BeforeUi</c>).</summary>
    public void AddViewportPasses(RenderGraph graph, float timeSeconds)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ObjectDisposedException.ThrowIf(_disposed, this);
        int rendered = 0;
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            s.RenderedThisFrame = false;
            ViewportControl c = s.Control;
            if (!c.IsRenderable || c.Camera is null)
            {
                continue;
            }

            int w = c.TargetPixelWidth;
            int h = c.TargetPixelHeight;
            if (w <= 0 || h <= 0)
            {
                continue;
            }

            // A freshly (re)allocated target holds memory no pass has written (and, on Vulkan, an UNDEFINED layout): it
            // is shown only from the first frame a renderer draws into it.
            if (s.Target.Ensure(w, h))
            {
                c.Image = null;
            }

            var frame = new ViewportFrame(s.Target.Color!.Handle, (uint)w, (uint)h, c.Camera, timeSeconds);
            ViewportPassOutput output = s.Renderer.AddPasses(graph, in frame);
            if (!output.Color.IsValid)
            {
                continue;
            }

            if (!ReferenceEquals(c.Image, s.Target.Image))
            {
                c.Image = s.Target.Image;
            }

            c.LastViewProjection = output.ViewProjection;
            s.ColorReadback?.AddCopyPass(graph, output.Color, s.Target.Color, w, h);
            _ui.AddSampledTexture(output.Color);
            s.FrameColor = output.Color;
            s.RenderedThisFrame = true;
            c.NotifyFrameChanged();
            rendered++;
        }

        RenderedLastFrame = rendered;
    }

    /// <summary>Returns every target rendered this frame to Common. Call AFTER the `ui` pass (<c>AppHost.AfterUi</c>).</summary>
    public void AddTerminalPasses(RenderGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        for (int i = 0; i < _slots.Count; i++)
        {
            Slot s = _slots[i];
            if (!s.RenderedThisFrame)
            {
                continue;
            }

            _terminalTarget = s.FrameColor;
            graph.AddPass("viewport-terminal", PassKind.Graphics, _terminalSetup);
            s.RenderedThisFrame = false;
        }
    }

    /// <summary>Harvests all readbacks (call after the GPU is idle).</summary>
    public void CompleteReadbacks()
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            _slots[i].ColorReadback?.Complete();
        }
    }

    /// <summary>Frees every target (call with the GPU idle or before the device's deferred queue drains).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            Detach(_slots[i].Control);
        }
    }

    private void TerminalSetup(PassBuilder s)
    {
        s.Write(_terminalTarget, ResourceState.Common, ShaderStage.None);
        s.SetExecute(NoOp);
    }

    private int Find(ViewportControl c)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (ReferenceEquals(_slots[i].Control, c))
            {
                return i;
            }
        }

        return -1;
    }

    private int Require(ViewportControl c)
    {
        int i = Find(c);
        return i >= 0 ? i : throw new InvalidOperationException("viewport is not attached");
    }

    private sealed class Slot
    {
        public Slot(ViewportControl control, IViewportSceneRenderer renderer, ViewportTarget target)
        {
            Control = control;
            Renderer = renderer;
            Target = target;
        }

        public ViewportControl Control { get; }

        public IViewportSceneRenderer Renderer { get; }

        public ViewportTarget Target { get; }

        public TextureReadback? ColorReadback { get; set; }

        public RgHandle FrameColor { get; set; }

        public bool RenderedThisFrame { get; set; }
    }
}
