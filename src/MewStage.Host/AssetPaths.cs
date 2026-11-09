// The ONE place the application locates its read-only asset tree (assets/: fonts, shaders, icons).
//
// Two layouts, one rule each:
//   * PACKAGED (a `dotnet publish` folder): <exe dir>/assets is the asset root and NOTHING else is searched: no walking
//     up, no current directory. A file missing from the package fails loudly instead of being quietly read from
//     whatever tree happens to sit above the install folder.
//   * DEVELOPMENT (dotnet build / run / test): the root is found by walking up from the exe dir, then from the current
//     directory, to the first `assets/` folder. Builds copy nothing, so development runs always read the live files.
// A directory only counts as the root when it holds the marker file (assets.marker). That matters on Windows, where
// folder names are case-insensitive: a stale bin\...\Assets\ left by an old build must not read as a package.
using System;
using System.IO;

namespace MewStage.Host;

/// <summary>Locates the asset root and the files under it. See the file header for the two layouts.</summary>
public static class AssetPaths
{
    /// <summary>Name of the asset folder (beside the exe in a package; at the repository root in development).</summary>
    public const string FolderName = "assets";

    /// <summary>A file every asset root contains. Identifies a real root.</summary>
    public const string Marker = "assets.marker";

    /// <summary>How far up the development walk goes from each start directory.</summary>
    public const int MaxWalkDepth = 12;

    /// <summary>The separator asset-relative spellings use on every platform.</summary>
    private const char AssetSeparator = '/';

    private static readonly Lazy<Location> CurrentLocation = new(() => Locate(AppContext.BaseDirectory, Directory.GetCurrentDirectory()));

    /// <summary>The result of locating the root.</summary>
    /// <param name="Root">Absolute path of the asset root, or null when none was found.</param>
    /// <param name="IsPackaged">True when the root is <c>&lt;exe dir&gt;/assets</c> (a published build).</param>
    public readonly record struct Location(string? Root, bool IsPackaged);

    /// <summary>The asset root of this process, or null when none exists.</summary>
    public static string? Root => CurrentLocation.Value.Root;

    /// <summary>True when running from a published package (<c>&lt;exe dir&gt;/assets</c>).</summary>
    public static bool IsPackaged => CurrentLocation.Value.IsPackaged;

    /// <summary>
    /// Locate the asset root for an exe in <paramref name="baseDirectory"/> started from <paramref name="currentDirectory"/>.
    /// Pure apart from file-system reads; the process uses <see cref="Root"/> and <see cref="IsPackaged"/>.
    /// </summary>
    public static Location Locate(string baseDirectory, string? currentDirectory)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseDirectory);

        // Packaged: the exe's own assets folder, and nothing else.
        string packaged = Path.Combine(baseDirectory, FolderName);
        if (IsRoot(packaged))
        {
            return new Location(Path.GetFullPath(packaged), IsPackaged: true);
        }

        // Development: walk up from the exe, then from the current directory.
        foreach (string? start in new[] { baseDirectory, currentDirectory })
        {
            string? dir = start;
            for (int i = 0; i < MaxWalkDepth && !string.IsNullOrEmpty(dir); i++)
            {
                string local = Path.Combine(dir, FolderName);
                if (IsRoot(local))
                {
                    return new Location(Path.GetFullPath(local), IsPackaged: false);
                }

                dir = Directory.GetParent(dir)?.FullName;
            }
        }

        return new Location(null, IsPackaged: false);
    }

    /// <summary>
    /// The path an asset would have under the root (whether or not it exists). With no root, the path under
    /// <c>&lt;exe dir&gt;/assets</c>, so a "file not found" names where it was expected.
    /// </summary>
    public static string PathFor(params string[] relative)
    {
        return Path.Combine(Root ?? Path.Combine(AppContext.BaseDirectory, FolderName), JoinRelative(relative));
    }

    /// <summary>The asset file at <paramref name="relative"/> under the root, or null when it does not exist.</summary>
    public static string? Find(params string[] relative)
    {
        if (Root is null)
        {
            return null;
        }

        string p = Path.Combine(Root, JoinRelative(relative));
        return File.Exists(p) ? p : null;
    }

    /// <summary>The asset directory at <paramref name="relative"/> under the root, or null when it does not exist.</summary>
    public static string? FindDirectory(params string[] relative)
    {
        if (Root is null)
        {
            return null;
        }

        string p = Path.Combine(Root, JoinRelative(relative));
        return Directory.Exists(p) ? p : null;
    }

    /// <summary>Like <see cref="Find"/>, but throws a <see cref="FileNotFoundException"/> that names the root searched.</summary>
    public static string Require(params string[] relative)
    {
        return Find(relative) ?? throw new FileNotFoundException(NotFoundMessage(Path.Combine(relative)), PathFor(relative));
    }

    /// <summary>A uniform "not found" message naming the root (or its absence) and the layout.</summary>
    public static string NotFoundMessage(string relative)
    {
        return Root is null
            ? $"asset '{relative}' not found: no asset root (looked for {FolderName}/{Marker} beside the exe, then above the exe dir and the current directory)"
            : $"asset '{relative}' not found under {(IsPackaged ? "the packaged" : "the development")} asset root {Root}";
    }

    // Callers pass asset-relative folders spelled with '/' ("shaders/cooked"); Path.Combine alone would keep that
    // literal '/' on Windows and yield a mixed-separator path.
    private static string JoinRelative(string[] relative)
    {
        string joined = Path.Combine(relative);
        return Path.DirectorySeparatorChar == AssetSeparator ? joined : joined.Replace(AssetSeparator, Path.DirectorySeparatorChar);
    }

    private static bool IsRoot(string dir)
    {
        return File.Exists(Path.Combine(dir, Marker));
    }
}
