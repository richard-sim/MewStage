using Aprillz.MewUI.Rendering;
using Aprillz.MewUI.Text;

namespace Aprillz.MewUI.MewCharts.Drawing;

/// <summary>
/// Ambient retained text layouts for chart geometries. LiveCharts measures label geometries
/// without a live frame context, so the owning graphics factory is captured when a chart attaches.
/// </summary>
public static class MewChartsText
{
    private static readonly object _lock = new();
    private static IGraphicsFactory? _factory;

    /// <summary>Font family for chart text; set from the chart's (inherited) <c>Control.FontFamily</c>.</summary>
    public static string FontFamily { get; set; } = ThemeMetrics.SystemFontFamily;

    /// <summary>
    /// Multiplier applied to every text size, set from the chart's <c>Control.FontSize</c> relative
    /// to the default (12). Lets the theme's per-role sizes scale with the inherited font size.
    /// </summary>
    public static double FontScale { get; set; } = 1;

    /// <summary>Wires the ambient text resources to a graphics factory (idempotent).</summary>
    public static void EnsureInitialized(IGraphicsFactory factory)
    {
        if (_factory is not null) return;
        lock (_lock)
        {
            if (_factory is not null) return;
            _factory = factory;
        }
    }

    /// <summary>Gets a retained layout of the given size, or <see langword="null"/> before init.</summary>
    public static ITextLayout? GetLayout(string text, float size)
    {
        if (_factory is null || string.IsNullOrEmpty(text)) return null;
        var family = string.IsNullOrEmpty(FontFamily) ? ThemeManager.DefaultMetrics.FontFamily : FontFamily;
        var scaled = (float)Math.Max(1, size * FontScale);
        lock (_lock)
        {
            return _factory.TextEngine.GetOrCreateLayout(
                new TextLayoutRequest
                {
                    Text = text.AsMemory(),
                    Dpi = 96,
                    DefaultStyle = new TextRunStyle(family, scaled)
                },
                TextLayoutCachePolicy.Content);
        }
    }

    /// <summary>Measures text without a frame context; returns zero size before init.</summary>
    public static Size Measure(string text, float size)
    {
        return GetLayout(text, size)?.MeasuredSize ?? Size.Empty;
    }
}
