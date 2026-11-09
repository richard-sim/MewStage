// ViewportControl: a 3D viewport inside the UI. The world renders OFFSCREEN (render-graph passes added by
// ViewportCompositor before the `ui` pass) into a target sized to this control's arranged rect in device pixels;
// OnRender draws that target as a MewVG image (retained-render model: the compositor calls NotifyFrameChanged ->
// InvalidateVisual every frame it re-rendered, since an unchanged OnRender is not re-run) and then the 2D overlay:
// text labels projected from world anchors with the frame's view-projection. Input goes to an IViewportInputSink
// (the application's camera controller); hovering gives the viewport "area focus" for hover-routed shortcuts without
// taking keyboard focus from a text field.
using System;
using System.Collections.Generic;
using System.Numerics;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;

using MewStage.Rendering.Camera;

namespace MewStage.Viewport;

/// <summary>Receives a viewport's input (an application's camera controller; tests feed synthetic input through it).
/// Positions are in the control's local DIPs.</summary>
public interface IViewportInputSink
{
    /// <summary>Pointer entered (true) or left (false) the viewport: area focus for hover-routed shortcuts.</summary>
    void OnHover(ViewportControl viewport, bool inside);

    /// <summary>A mouse button went down.</summary>
    void OnMouseDown(ViewportControl viewport, MouseEventArgs e);

    /// <summary>A mouse button went up.</summary>
    void OnMouseUp(ViewportControl viewport, MouseEventArgs e);

    /// <summary>The pointer moved.</summary>
    void OnMouseMove(ViewportControl viewport, MouseEventArgs e);

    /// <summary>The wheel turned.</summary>
    void OnMouseWheel(ViewportControl viewport, MouseWheelEventArgs e);

    /// <summary>A key went down while the viewport had keyboard focus.</summary>
    void OnKeyDown(ViewportControl viewport, KeyEventArgs e);

    /// <summary>A key went up while the viewport had keyboard focus.</summary>
    void OnKeyUp(ViewportControl viewport, KeyEventArgs e);
}

/// <summary>A text label anchored to a world point, drawn by the control over its image.</summary>
public sealed record ViewportLabel(Vector3 Anchor, string Text, Color Color);

/// <summary>The 3D viewport control (see the file header).</summary>
public class ViewportControl : Control
{
    // MewUI's DPI unit: GetDpi() reports dots per inch and 96 DPI is one device pixel per DIP.
    private const double DipDpi = 96.0;

    private static readonly TextParagraphStyle LabelParagraph = new() { Wrapping = TextWrapping.NoWrap };

    private readonly List<ViewportLabel> _labels = [];

    // Label layouts are built once per label revision / DPI / font and reused every frame (a layout request is a record
    // class: building one per label per frame would allocate).
    private ITextLayout?[] _labelLayouts = [];
    private long _labelRevision = -1;
    private long _labelsBuiltAt;
    private uint _labelDpi;
    private double _labelFontSize;

    static ViewportControl()
    {
        FocusableProperty.OverrideDefaultValue<ViewportControl>(true);
    }

    /// <summary>Raised when <see cref="IsHovered"/> changes.</summary>
    public event Action<ViewportControl, bool>? HoverChanged;

    /// <summary>The controller that receives this viewport's input, or null.</summary>
    public IViewportInputSink? InputSink { get; set; }

    /// <summary>The camera the world renders with (null = nothing renders).</summary>
    public RenderCamera? Camera { get; set; }

    /// <summary>The labels drawn over the image (edit then call <see cref="InvalidateLabels"/>).</summary>
    public IList<ViewportLabel> Labels => _labels;

    /// <summary>The view-projection of the last rendered frame (labels project with it).</summary>
    public Matrix4x4 LastViewProjection { get; set; } = Matrix4x4.Identity;

    /// <summary>True when the camera uses the D3D / Metal clip convention (clip +Y up); false for Vulkan.</summary>
    public bool ClipYUp => Camera is null || Camera.Clip != ClipConvention.Vulkan;

    /// <summary>True while the pointer is over the viewport (area focus).</summary>
    public bool IsHovered { get; private set; }

    /// <summary>The image drawn over the control's bounds (the composited offscreen world target); null draws nothing.</summary>
    public IImage? Image
    {
        get;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                InvalidateVisual();
            }
        }
    }

    /// <summary>Number of times the world image was declared changed (tests).</summary>
    public long FrameChangeCount { get; private set; }

    /// <summary>Device pixels per DIP of this control's window.</summary>
    public double PixelsPerDip
    {
        get
        {
            uint dpi = GetDpi();
            return dpi == 0 ? 1.0 : dpi / DipDpi;
        }
    }

    /// <summary>Target width in device pixels for the current arranged size (0 before the first arrange).</summary>
    public int TargetPixelWidth => (int)Math.Round(Bounds.Width * PixelsPerDip);

    /// <summary>Target height in device pixels for the current arranged size (0 before the first arrange).</summary>
    public int TargetPixelHeight => (int)Math.Round(Bounds.Height * PixelsPerDip);

    /// <summary>True when the control is visible inside a window and has a non-empty arranged rect.</summary>
    public bool IsRenderable => IsVisible && FindVisualRoot() is Window && Bounds.Width > 0 && Bounds.Height > 0;

    /// <summary>Tells MewUI the world image changed this frame (retained-render model: redraw without re-layout).</summary>
    public void NotifyFrameChanged()
    {
        FrameChangeCount++;
        InvalidateVisual();
    }

    /// <summary>Call after changing <see cref="Labels"/>: the label layouts are rebuilt on the next render.</summary>
    public void InvalidateLabels()
    {
        _labelRevision++;
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override Size MeasureContent(Size availableSize)
    {
        return new Size(double.IsFinite(availableSize.Width) ? availableSize.Width : 0, double.IsFinite(availableSize.Height) ? availableSize.Height : 0);
    }

    /// <inheritdoc />
    protected override void OnRender(IGraphicsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (Image is { } img)
        {
            context.DrawImage(img, Bounds);
        }

        DrawLabels(context);
    }

    /// <inheritdoc />
    protected override void OnMouseEnter()
    {
        base.OnMouseEnter();
        SetHovered(true);
    }

    /// <inheritdoc />
    protected override void OnMouseLeave()
    {
        base.OnMouseLeave();
        SetHovered(false);
    }

    /// <inheritdoc />
    /// <remarks>The viewport takes the mouse capture on a press and handles the event, so the moves and the RELEASE
    /// come back here wherever the pointer goes (an orbit drag always sees its button-up, also over another pane),
    /// and no ancestor (the dock's pane / tab drag) takes the press and with it the rest of the gesture.</remarks>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        ArgumentNullException.ThrowIfNull(e);
        InputSink?.OnMouseDown(this, e);
        if (!IsMouseCaptured && FindVisualRoot() is Window w)
        {
            w.CaptureMouse(this);
        }

        e.Handled = true;
    }

    /// <inheritdoc />
    /// <remarks>Releases the capture when the last button comes up (see <see cref="OnMouseDown"/>).</remarks>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        ArgumentNullException.ThrowIfNull(e);
        InputSink?.OnMouseUp(this, e);
        if (!e.LeftButton && !e.RightButton && !e.MiddleButton && IsMouseCaptured && FindVisualRoot() is Window w)
        {
            w.ReleaseMouseCapture();
        }

        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        InputSink?.OnMouseMove(this, e);
    }

    /// <inheritdoc />
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        InputSink?.OnMouseWheel(this, e);
    }

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        InputSink?.OnKeyDown(this, e);
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        InputSink?.OnKeyUp(this, e);
    }

    private void SetHovered(bool inside)
    {
        if (IsHovered == inside)
        {
            return;
        }

        IsHovered = inside;
        InputSink?.OnHover(this, inside);
        HoverChanged?.Invoke(this, inside);
    }

    private void EnsureLabelLayouts()
    {
        uint dpi = GetDpi();
        if (_labelsBuiltAt == _labelRevision && _labelDpi == dpi && _labelFontSize == FontSize && _labelLayouts.Length == _labels.Count)
        {
            return;
        }

        if (_labelLayouts.Length != _labels.Count)
        {
            _labelLayouts = new ITextLayout?[_labels.Count];
        }

        ITextEngine engine = GetGraphicsFactory().TextEngine;
        for (int i = 0; i < _labels.Count; i++)
        {
            _labelLayouts[i] = engine.CreateLayout(new TextLayoutRequest
            {
                Text = _labels[i].Text.AsMemory(),
                Dpi = dpi,
                Paragraph = LabelParagraph,
                DefaultStyle = new TextRunStyle(FontFamily, FontSize, FontWeight, false),
            });
        }

        _labelsBuiltAt = _labelRevision;
        _labelDpi = dpi;
        _labelFontSize = FontSize;
    }

    private void DrawLabels(IGraphicsContext context)
    {
        if (_labels.Count == 0)
        {
            return;
        }

        EnsureLabelLayouts();
        Matrix4x4 vp = LastViewProjection;
        Rect b = Bounds;
        for (int i = 0; i < _labels.Count; i++)
        {
            ViewportLabel l = _labels[i];
            if (_labelLayouts[i] is not { } layout)
            {
                continue;
            }

            if (!ViewportMath.Project(l.Anchor, in vp, (float)b.Width, (float)b.Height, ClipYUp, out Vector2 p))
            {
                continue;
            }

            var options = new TextDrawOptions(l.Color, Owner: this);
            context.Text.Draw(layout, new Point(b.X + p.X, b.Y + p.Y), in options);
        }
    }
}
