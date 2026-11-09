// The window icon of every SDL window the application opens. An application names its icon set once
// (WindowIcon.Use(new IconSet("icons/branding", "mewstage"))) before its first window; GpuWindow and SecondaryWindow
// then apply it to every window they create. The icon files are PNGs: assets/<Directory>/<Stem>-<size>.png for each
// of Sizes. The 32 px image is the window's icon and the others are its alternate images, so SDL picks the best fit
// for the title bar (16 / 24), the taskbar and Alt-Tab (32 / 48 / 256) and a high-DPI display
// (SDL_AddSurfaceAlternateImage).
//
// Never throws and never crashes: a platform without window icons (the SDL offscreen / dummy drivers, Wayland without
// the protocol), a missing file or a decode failure makes Apply report Accepted = false (with one stderr line per
// cause). The exe's own icon (Explorer, the taskbar button before a window exists) is the .ico file referenced by
// <ApplicationIcon>.
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using SDL;

using static SDL.SDL3;

namespace MewStage.Host;

/// <summary>A set of window-icon PNGs: <c>assets/&lt;Directory&gt;/&lt;Stem&gt;-&lt;size&gt;.png</c> for each of <see cref="WindowIcon.Sizes"/>.</summary>
public sealed record IconSet(string Directory, string Stem);

/// <summary>Sets SDL window icons from a PNG icon set (see the file header).</summary>
public static unsafe class WindowIcon
{
    /// <summary>The size whose image is the window's icon proper (the others are alternates): the Windows large-icon size at 100 %.</summary>
    public const int PrimarySize = 32;

    private static readonly int[] SizeList = [16, 24, 32, 48, 64, 128, 256];

    private static readonly HashSet<string> Warned = [];

    /// <summary>The image sizes of an icon set, pixels.</summary>
    public static ReadOnlySpan<int> Sizes => SizeList;

    /// <summary>The set windows created from now on get (null = none; an application that names no set opens icon-less windows).</summary>
    public static IconSet? Current { get; private set; }

    /// <summary>
    /// What <see cref="Apply(SDL_Window*, IconSet?)"/> did: how many images of the set decoded, whether SDL accepted the
    /// icon (SDL_SetWindowIcon returned true: the OS window now shows it) and the reason when not.
    /// </summary>
    public readonly record struct Result(int Images, bool Accepted, string? Error)
    {
        /// <summary>Every image of the set decoded.</summary>
        public bool Decoded => Images == SizeList.Length;
    }

    /// <summary>Name the icon set of this process's windows (null = none).</summary>
    public static void Use(IconSet? set)
    {
        Current = set;
    }

    /// <summary>Set <paramref name="window"/>'s icon from <see cref="Current"/>.</summary>
    public static Result Apply(SDL_Window* window)
    {
        return Apply(window, Current);
    }

    /// <summary>
    /// Set <paramref name="window"/>'s icon from <paramref name="set"/>. Never throws: a null window / set, a missing or
    /// undecodable file or a driver without window icons (SDL's offscreen and dummy drivers) is reported in the result
    /// (<see cref="Result.Accepted"/> false) and the window keeps the default icon.
    /// </summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "An icon is cosmetic: whatever goes wrong loading it is reported and the window opens without it.")]
    public static Result Apply(SDL_Window* window, IconSet? set)
    {
        if (window == null || set is null)
        {
            return new Result(0, false, "no window or no icon set");
        }

        SDL_Surface* primary = null;
        int images = 0;
        try
        {
            var alternates = new List<nint>();
            foreach (int size in SizeList)
            {
                string file = set.Stem + "-" + size.ToString(CultureInfo.InvariantCulture) + ".png";
                string? path = AssetPaths.Find(set.Directory, file);
                if (path is null)
                {
                    Warn("icon file " + set.Directory + "/" + file + " not found: " + AssetPaths.NotFoundMessage(set.Directory + "/" + file));
                    continue;
                }

                SDL_Surface* surface = SDL_LoadPNG(path);
                if (surface == null)
                {
                    Warn("icon file " + path + " did not decode: " + SDL_GetError());
                    continue;
                }

                images++;
                if (size == PrimarySize)
                {
                    primary = surface;
                }
                else
                {
                    alternates.Add((nint)surface);
                }
            }

            foreach (nint a in alternates)
            {
                var s = (SDL_Surface*)a;
                if (primary != null)
                {
                    SDL_AddSurfaceAlternateImage(primary, s);
                }

                SDL_DestroySurface(s);   // the primary holds its own reference
            }

            if (primary == null)
            {
                return new Result(images, false, "the " + PrimarySize.ToString(CultureInfo.InvariantCulture) + " px image is missing");
            }

            if (!SDL_SetWindowIcon(window, primary))
            {
                string error = SDL_GetError() ?? "unknown";
                Warn("SDL_SetWindowIcon: " + error);
                return new Result(images, false, error);
            }

            return new Result(images, true, null);
        }
        catch (Exception e)
        {
            Warn("window icon: " + e.Message);
            return new Result(images, false, e.Message);
        }
        finally
        {
            if (primary != null)
            {
                SDL_DestroySurface(primary);
            }
        }
    }

    private static void Warn(string message)
    {
        if (Warned.Add(message))
        {
            Console.Error.WriteLine("[window-icon] " + message + " - the window keeps the default icon.");
        }
    }
}
