// The demo's SVG icons (assets/icons/*.svg), parsed once per process through MewUI.Svg and shared: an SvgImageSource
// is a vector source, so one instance draws at any size in the HUD legend (design space), the Workbench toolbar (DIP)
// and the Kit page. A missing file yields null and the caller shows text instead.
//
// Images use MewUI's default vector cache mode (ImageVectorCacheMode.Cached): the SVG is rasterized once, at the
// control's pixel size, into a scratch surface the embedded backend renders inside the `ui` pass
// (MewStage.Ui.Backend/UiScratchSurface.cs), and blitted as a texture on the frames after; a size or DPI change
// rasterizes again. ImageVectorCacheMode.Direct (draw the paths every repaint, no cache) still works and is what an
// icon that animates its size every frame would want.
using System;
using System.Collections.Concurrent;

using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Svg;

using MewStage.Host;

namespace MewStage.Demo;

/// <summary>Named access to <c>assets/icons</c> (see the file header).</summary>
internal static class DemoIcons
{
    /// <summary>Every icon the assets folder is expected to hold.</summary>
    public static readonly string[] Names = ["new", "open", "save", "play", "pause", "camera", "settings", "close"];

    private static readonly ConcurrentDictionary<string, SvgImageSource?> Cache = new(StringComparer.Ordinal);

    /// <summary>An <see cref="Image"/> of icon <paramref name="name"/> at <paramref name="size"/> DIP (cached, see the file header), or null when the file is missing.</summary>
    public static Image? Icon(string name, double size)
    {
        if (Get(name) is not { } svg)
        {
            return null;
        }

        return new Image().Source(svg).Size(size);
    }

    /// <summary>The icon <paramref name="name"/> (file stem), or null when the file is not in the assets.</summary>
    public static SvgImageSource? Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Cache.GetOrAdd(name, static n =>
        {
            string? path = AssetPaths.Find("icons", n + ".svg");
            return path is null ? null : SvgImageSource.FromFile(path);
        });
    }
}
