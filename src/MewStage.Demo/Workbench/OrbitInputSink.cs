// The viewport's camera controller: left-drag orbits, middle-drag (or Shift + left-drag) pans, the wheel zooms, F
// frames the cube. Positions arrive in the control's DIPs; the camera is the demo's OrbitCamera, which produces the
// RenderCamera the compositor hands to the renderer each frame.
using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Rendering.Camera;
using MewStage.Scene;
using MewStage.Viewport;

namespace MewStage.Demo.Workbench;

/// <summary>Orbit / pan / zoom / frame for one viewport (see the file header).</summary>
internal sealed class OrbitInputSink : IViewportInputSink
{
    private const float OrbitRadiansPerDip = 0.008f;

    private readonly CubeScene _scene;
    private Point _last;
    private bool _orbiting;
    private bool _panning;

    public OrbitInputSink(OrbitCamera camera, CubeScene scene, ClipConvention clip)
    {
        Camera = camera ?? throw new ArgumentNullException(nameof(camera));
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        Clip = clip;
    }

    /// <summary>The camera this sink drives.</summary>
    public OrbitCamera Camera { get; }

    /// <summary>The backend's clip convention for <see cref="Render"/>.</summary>
    public ClipConvention Clip { get; }

    /// <summary>Orbit gestures seen (tests).</summary>
    public int Orbits { get; private set; }

    /// <summary>Zoom notches seen (tests).</summary>
    public int Zooms { get; private set; }

    /// <summary>Pan gestures seen (tests).</summary>
    public int Pans { get; private set; }

    /// <summary>The camera for this frame.</summary>
    public RenderCamera Render()
    {
        return Camera.ToRenderCamera(Clip);
    }

    /// <summary>Frame the cube: look at its center from a distance that shows all of it.</summary>
    public void FrameCube()
    {
        Camera.Frame(_scene.Center, _scene.Radius);
    }

    /// <inheritdoc />
    public void OnHover(ViewportControl viewport, bool inside)
    {
    }

    /// <inheritdoc />
    public void OnMouseDown(ViewportControl viewport, MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!IsConnected(viewport))
        {
            return;
        }

        _last = e.GetPosition(viewport);
        bool shift = (e.Modifiers & ModifierKeys.Shift) != 0;
        if (e.Button == MouseButton.Middle || (e.Button == MouseButton.Left && shift))
        {
            _panning = true;
        }
        else if (e.Button == MouseButton.Left)
        {
            _orbiting = true;
        }

        viewport.Focus();
    }

    /// <inheritdoc />
    public void OnMouseUp(ViewportControl viewport, MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Button == MouseButton.Left)
        {
            _orbiting = false;
            _panning = false;
        }

        if (e.Button == MouseButton.Middle)
        {
            _panning = false;
        }
    }

    /// <inheritdoc />
    public void OnMouseMove(ViewportControl viewport, MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (!IsConnected(viewport) || (!_orbiting && !_panning))
        {
            return;
        }

        Point p = e.GetPosition(viewport);
        float dx = (float)(p.X - _last.X);
        float dy = (float)(p.Y - _last.Y);
        _last = p;
        if (_panning)
        {
            Camera.Pan(dx, dy);
            Pans++;
        }
        else
        {
            Camera.Orbit(-dx * OrbitRadiansPerDip, dy * OrbitRadiansPerDip);
            Orbits++;
        }
    }

    /// <inheritdoc />
    public void OnMouseWheel(ViewportControl viewport, MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Delta.Y != 0)
        {
            Camera.Zoom((float)e.Delta.Y);
            Zooms++;
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    public void OnKeyDown(ViewportControl viewport, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == Key.F && !e.Handled)
        {
            FrameCube();
            e.Handled = true;
        }
    }

    /// <inheritdoc />
    public void OnKeyUp(ViewportControl viewport, KeyEventArgs e)
    {
    }

    // MouseEventArgs.GetPosition throws for an element outside a window with a native handle (a viewport detached
    // inside the very event that asks for the position).
    private static bool IsConnected(UIElement viewport)
    {
        return viewport.FindVisualRoot() is Window { Handle: not 0 };
    }
}
