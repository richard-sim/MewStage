// The geometry sample: a rectangle and an ellipse combined with each of MewUI.Geometry's Boolean operations
// (PathGeometryOperations.Combine: union, intersect, xor, exclude) and drawn as filled paths with their outlines.
// The combination runs on the CPU (Clipper on flattened paths) and is backend-independent, so the same result shows
// on the platform host here and on the embedded host; the control recomputes only when its size changes.
using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Geometry;
using Aprillz.MewUI.Rendering;

namespace MewStage.Demo.Kit;

/// <summary>Four Boolean combinations of a rectangle and an ellipse (see the file header).</summary>
internal sealed class GeometrySample : Control
{
    /// <summary>The operations drawn, left to right.</summary>
    public static readonly GeometryCombineMode[] Modes =
    [
        GeometryCombineMode.Union,
        GeometryCombineMode.Intersect,
        GeometryCombineMode.Xor,
        GeometryCombineMode.Exclude,
    ];

    private static readonly Color Fill = Color.FromRgb(94, 156, 255);
    private static readonly Color Outline = Color.FromRgb(230, 233, 239);
    private static readonly Color Ghost = Color.FromRgb(90, 96, 110);

    private readonly PathGeometry?[] _results = new PathGeometry?[Modes.Length];
    private Size _builtFor;

    /// <summary>The cell size each operation gets, DIP.</summary>
    public const double Cell = 150;

    /// <summary>The combined paths of the last build (null before the first render).</summary>
    public PathGeometry? Result(int index)
    {
        return _results[index];
    }

    /// <summary>The two input shapes inside a cell of <paramref name="size"/> DIP: a square top-left, a circle overlapping its bottom-right.</summary>
    public static (RectangleGeometry Rect, EllipseGeometry Ellipse) Shapes(double size)
    {
        return (new RectangleGeometry(new Rect(size * 0.12, size * 0.12, size * 0.5, size * 0.5)),
                new EllipseGeometry(new Rect(size * 0.34, size * 0.34, size * 0.54, size * 0.54)));
    }

    /// <summary>Combine the sample shapes inside a cell of <paramref name="size"/> DIP.</summary>
    public static PathGeometry Combine(GeometryCombineMode mode, double size)
    {
        (RectangleGeometry rect, EllipseGeometry ellipse) = Shapes(size);
        return PathGeometryOperations.Combine(rect, ellipse, mode);
    }

    /// <inheritdoc />
    protected override Size MeasureContent(Size availableSize)
    {
        return new Size(Cell * Modes.Length, Cell + 28);
    }

    /// <inheritdoc />
    protected override void OnRender(IGraphicsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Size size = new(Bounds.Width, Bounds.Height);
        if (size != _builtFor)
        {
            for (int i = 0; i < Modes.Length; i++)
            {
                _results[i] = Combine(Modes[i], Cell);
            }

            _builtFor = size;
        }

        double x = Bounds.X;
        double y = Bounds.Y;
        for (int i = 0; i < Modes.Length; i++)
        {
            double ox = x + (i * Cell);
            PathGeometry? result = _results[i];
            if (result is null)
            {
                continue;
            }

            // The two inputs as thin ghosts, then the combination filled and outlined.
            context.DrawRectangle(new Rect(ox + (Cell * 0.12), y + (Cell * 0.12), Cell * 0.5, Cell * 0.5), Ghost, 1);
            context.DrawEllipse(new Rect(ox + (Cell * 0.34), y + (Cell * 0.34), Cell * 0.54, Cell * 0.54), Ghost, 1);
            PathGeometry shifted = result.Transform(System.Numerics.Matrix3x2.CreateTranslation((float)ox, (float)y));
            context.FillPath(shifted, Color.FromArgb(150, Fill.R, Fill.G, Fill.B));
            context.DrawPath(shifted, Outline, 2);
        }
    }
}
