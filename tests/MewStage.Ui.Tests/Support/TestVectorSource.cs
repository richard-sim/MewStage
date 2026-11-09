using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Rendering;

namespace MewStage.Ui.Tests.Support;

/// <summary>A vector image source the tests can see into: a filled rectangle of one color over the whole destination,
/// with a counter of how many times MewUI asked it to render (one per rasterization into a cache, one per frame when
/// drawn direct).</summary>
public sealed class TestVectorSource : IVectorImageSource
{
    private readonly Color _color;

    /// <summary>Creates a source of the given intrinsic size and fill color.</summary>
    public TestVectorSource(double width, double height, Color color)
    {
        IntrinsicSize = new Size(width, height);
        _color = color;
    }

    /// <summary>How many times <see cref="Render"/> ran.</summary>
    public int Renders { get; private set; }

    /// <inheritdoc />
    public Size IntrinsicSize { get; }

    /// <inheritdoc />
    public IImage CreateImage(IGraphicsFactory factory)
    {
        throw new NotSupportedException("a vector source is drawn, not decoded");
    }

    /// <inheritdoc />
    public void Render(IGraphicsContext context, Rect destRect)
    {
        ArgumentNullException.ThrowIfNull(context);
        Renders++;
        context.FillRectangle(destRect, _color);
    }
}
