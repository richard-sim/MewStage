// MewStageMewVGGraphicsContext.Platform.cs: the MewStage partial of upstream's MewVG IGraphicsContext bridge
// (third_party/MewUI/src/MewUI/Shared/Rendering/MewVG/MewVGGraphicsContext.Common.cs, compiled in this assembly under
// MEWUI_MEWVG_MEWSTAGE, the core-bridge vendor patch). The Common file draws every shape, path, brush and clip onto
// `_vg`; this file supplies what the GL partials supply upstream, for the application's GPU device:
//
//   * Frame bracket. The context is KEPT across frames (Window.RenderFrameToPresentSurface), so BeginFramePlatform pulls
//     this frame's recorder from the surface instead of being bound once: a context made per frame would re-realize
//     every text run every frame.
//   * The scratch bracket (AttachScratch): a context over a UiScratchSurface, which MewUI makes per rasterization of a
//     vector image or a bitmap cache and runs from OnRender, inside the host's `ui` pass execute and before that pass
//     opens its render pass. BeginFrame binds the device's shared scratch MewVG context to the frame's recorder;
//     EndFrame records the render right there: the scratch context's pending uploads, the surface's own
//     BeginRendering, the flush, EndRendering, the barrier to ShaderResource (UiScratchSurface.cs).
//   * Text, under MewUI's backend text contract: MeasureText, CreateBackendTextLayout, DrawBackendTextLayout. Runs are
//     drawn with FontStashSharp at the run origin (the managed engine has already laid them out with the same
//     advances, see UiMeasurementContext), through the current transform and clipped to the clip bounds.
//   * Images: MewVGImage's image path and the Common file's DrawImagePattern.

using System.Numerics;

using MewStage.Graphics;
using MewStage.Ui.Backend;

namespace Aprillz.MewUI.Rendering.MewVG;

internal sealed partial class MewStageMewVGGraphicsContext
{
    private UiRenderSurface? _surface;
    private UiScratchSurface? _scratch;
    private UiRenderDevice? _scratchDevice;
    private FontStashTextRenderer _text = null!;

    /// <summary>Attach this context to the window surface it draws into (once, at creation).</summary>
    internal void AttachSurface(UiRenderSurface surface)
    {
        _surface = surface;
    }

    /// <summary>Attach this context to a scratch surface (once, at creation); <paramref name="device"/> owns the
    /// shared scratch MewVG context and the frame being recorded.</summary>
    internal void AttachScratch(UiScratchSurface scratch, UiRenderDevice device)
    {
        _scratch = scratch;
        _scratchDevice = device;
    }

    partial void BeginFramePlatform()
    {
        if (_scratch is not null)
        {
            BeginScratchFrame();
            return;
        }

        UiRenderSurface surface = _surface ?? throw new InvalidOperationException("MewStageMewVGGraphicsContext has no surface");
        IGraphicsRecorder recorder = surface.Recorder
            ?? throw new InvalidOperationException("the UI surface has no recorder this frame (call BeginGraphRecording inside the ui pass)");
        UiMewVGContext vg = surface.Vg;
        _vg = vg;
        _text = surface.Text;
        vg.Begin(recorder);
        _vg.BeginFrame((float)_viewportWidthDip, (float)_viewportHeightDip, (float)DpiScale);
        _vg.ResetTransform();
        _vg.ResetScissor();
    }

    partial void EndFramePlatform()
    {
        if (_scratch is not null)
        {
            EndScratchFrame();
            return;
        }

        // EndFrame -> Flush: records the frame's fills, strokes and text onto the recorder (inside a window batch it
        // only appends; the surface's EndWindowBatch flushes once).
        _vg.EndFrame();
    }

    partial void DestroyPlatform()
    {
        // The MewVG context and text renderer are the surface's (or the device's) and outlive this context.
        _surface = null;
        _scratch = null;
        _scratchDevice = null;
        _text = null!;
    }

    private void BeginScratchFrame()
    {
        UiRenderDevice device = _scratchDevice!;
        IGraphicsRecorder recorder = device.FrameRecorder
            ?? throw new InvalidOperationException("a scratch surface (vector image cache, bitmap cache) can only be rendered inside the ui pass: the embedded host records GPU work per frame, and MewUI's off-thread rasterization is not supported here");
        UiMewVGContext vg = device.ScratchVg;
        _vg = vg;
        _text = device.ScratchText;
        vg.Begin(recorder);
        _vg.BeginFrame((float)_viewportWidthDip, (float)_viewportHeightDip, (float)DpiScale);
        _vg.ResetTransform();
        _vg.ResetScissor();
    }

    private void EndScratchFrame()
    {
        UiRenderDevice device = _scratchDevice!;
        UiScratchSurface scratch = _scratch!;
        if (device.FrameRecorder is not { } recorder || device.FrameCommandList is not { } cmd)
        {
            return;   // BeginFrame refused (no frame open); the context is being torn down
        }

        // Glyph-atlas and mask uploads this render queued go first (copies are not allowed inside a render pass),
        // then the render itself, recorded into the frame ahead of the `ui` pass that samples the result.
        device.ScratchVg.Renderer.RecordPendingUploads(cmd);
        scratch.BeginRender(cmd, recorder);
        try
        {
            _vg.EndFrame();
        }
        finally
        {
            scratch.EndRender(cmd, recorder);
            device.CountScratchRender();
        }
    }

    #region Text (the backend text contract)

    public override Size MeasureText(ReadOnlySpan<char> text, IFont font)
    {
        return UiMeasurementContext.Measure(text, font);
    }

    // The managed text engine breaks lines and hands this backend one run at a time, so a width limit never applies.
    public override Size MeasureText(ReadOnlySpan<char> text, IFont font, double maxWidth)
    {
        return UiMeasurementContext.Measure(text, font);
    }

    public override BackendTextLayout CreateBackendTextLayout(ReadOnlySpan<char> text, BackendTextFormat format, in BackendTextLayoutConstraints constraints)
    {
        Rect bounds = constraints.Bounds;
        double maxWidth = double.IsPositiveInfinity(bounds.Width) ? 0 : bounds.Width;
        Size measured = MeasureText(text, format.Font);
        double width = maxWidth > 0 ? maxWidth : measured.Width;
        double height = double.IsPositiveInfinity(bounds.Height) || bounds.Height <= 0 ? measured.Height : bounds.Height;
        return new BackendTextLayout
        {
            MeasuredSize = measured,
            EffectiveBounds = new Rect(bounds.X, bounds.Y, width, height),
            EffectiveMaxWidth = width,
            ContentHeight = measured.Height,
        };
    }

    public override void DrawBackendTextLayout(ReadOnlySpan<char> text, BackendTextFormat format, BackendTextLayout layout, Color color)
    {
        if (text.IsEmpty || color.A == 0)
        {
            return;
        }

        if (format.Font is not UiFont font)
        {
            throw new ArgumentException($"font {format.Font?.GetType().Name ?? "null"} was not created by the MewStage graphics factory");
        }

        Rect bounds = layout.EffectiveBounds;
        Size measured = layout.MeasuredSize;
        double boxW = bounds.Width > 0 ? bounds.Width : measured.Width;
        double boxH = bounds.Height > 0 ? bounds.Height : measured.Height;
        double x = bounds.X + format.HorizontalAlignment switch
        {
            TextAlignment.Center => (boxW - measured.Width) / 2,
            TextAlignment.Right => boxW - measured.Width,
            _ => 0,
        };
        double y = bounds.Y + format.VerticalAlignment switch
        {
            TextAlignment.Center => (boxH - measured.Height) / 2,
            TextAlignment.Bottom => boxH - measured.Height,
            _ => 0,
        };
        // Snap the run origin to the DEVICE grid (transform included), as the desktop backends do, so glyphs map
        // texel-for-pixel instead of being bilinearly smeared at a fractional offset.
        if (_textPixelSnap)
        {
            Point snapped = RenderingUtil.SnapTextOriginToDevice(new Point(x, y), _transform, DpiScale);
            x = snapped.X;
            y = snapped.Y;
        }

        NanoVgRenderer.TextClip clip = _clipBoundsWorld is Rect c
            ? new NanoVgRenderer.TextClip((float)c.X, (float)c.Y, (float)c.Width, (float)c.Height, (float)DpiScale)
            : default;
        _text.DrawText(UiTextIntern.Get(text), font, new Vector2((float)x, (float)y), ToNvgColor(color), in _transform, in clip, _globalAlpha);
    }

    #endregion

    #region Images

    public override void DrawImage(IImage image, Point location)
    {
        ArgumentNullException.ThrowIfNull(image);
        DrawImageCore(image, new Rect(location.X, location.Y, image.PixelWidth, image.PixelHeight));
    }

    protected override void DrawImageCore(IImage image, Rect destRect)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is not MewVGImage vgImage)
        {
            return;
        }

        int imageId = vgImage.GetOrCreateImageId(_vg, GetImageFlags());
        if (imageId == 0)
        {
            return;
        }

        DrawImagePattern(imageId, destRect, alpha: 1f, sourceRect: null, vgImage.PixelWidth, vgImage.PixelHeight);
    }

    protected override void DrawImageCore(IImage image, Rect destRect, Rect sourceRect)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image is not MewVGImage vgImage)
        {
            return;
        }

        int imageId = vgImage.GetOrCreateImageId(_vg, GetImageFlags());
        if (imageId == 0)
        {
            return;
        }

        DrawImagePattern(imageId, destRect, alpha: 1f, sourceRect: sourceRect, vgImage.PixelWidth, vgImage.PixelHeight);
    }

    #endregion
}
