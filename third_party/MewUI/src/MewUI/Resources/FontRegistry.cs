using System.Collections.Concurrent;

namespace Aprillz.MewUI.Resources;

/// <summary>
/// Global registry mapping (familyName) → the cached files of that family's faces.
/// Populated by <see cref="FontResources.Register"/> and queried by each rendering backend's CreateFont.
/// </summary>
/// <remarks>
/// MewStage vendor patch text/0003: a family may have several faces (Regular, Bold Italic, ...), each registered from its own
/// file under the same family name. The registry keeps every face with the weight and style its file declares
/// (OpenTypeFaceStyle) and picks one for a requested weight and style the way CSS font matching does; it used to keep
/// only the last file registered under a name, so every weight and style of a family drew with that one face.
/// </remarks>
internal static class FontRegistry
{
    internal readonly record struct ResolvedFont(string FamilyName, string FilePath);

    private readonly record struct Face(string FilePath, int Weight, bool Italic);

    // Key: family name (case-insensitive)
    // Value: the family's faces, in registration order (replaced whole, so a reader never sees a list being built)
    private static readonly ConcurrentDictionary<string, Face[]> _map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    private static int _version;

    /// <summary>Changes whenever a family is registered, so a cache of resolved font files knows to start over.</summary>
    internal static int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Registers a font file as a face of <paramref name="familyName"/>. The face's weight and style are read from
    /// the file; registering the same file again changes nothing.
    /// </summary>
    internal static void Register(string familyName, string filePath)
    {
        if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(filePath))
            return;

        var style = OpenTypeFaceStyle.Read(filePath);
        var face = new Face(filePath, style.Weight, style.Italic);
        lock (_gate)
        {
            if (_map.TryGetValue(familyName, out var faces))
            {
                if (Array.Exists(faces, f => string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase)))
                    return;
                _map[familyName] = [.. faces, face];
            }
            else
            {
                _map[familyName] = [face];
            }
        }
        Interlocked.Increment(ref _version);
    }

    /// <summary>
    /// Resolves a family name to a registered font file: the family's regular face (weight 400, upright), or the face
    /// nearest to it. Returns null if no registered font matches.
    /// </summary>
    internal static ResolvedFont? Resolve(string family) => Resolve(family, FontWeight.Normal, italic: false);

    /// <summary>
    /// Resolves a family name, weight and style to the registered face CSS font matching would pick (CSS Fonts 4
    /// §5.2): the requested style first (italic, else upright), then the weight - for 400 to 500 the weights up to 500
    /// ascending, then the lighter ones descending, then the heavier ones; below 400 the lighter ones descending, then
    /// the heavier; above 500 the heavier ones ascending, then the lighter. Returns null if no registered font matches.
    /// </summary>
    internal static ResolvedFont? Resolve(string family, FontWeight weight, bool italic)
    {
        if (string.IsNullOrWhiteSpace(family) || !_map.TryGetValue(family, out var faces) || faces.Length == 0)
            return null;

        var styled = Array.FindAll(faces, f => f.Italic == italic);
        if (styled.Length == 0)
            styled = faces;

        int want = (int)weight;
        Face best = styled[0];
        long bestRank = long.MaxValue;
        foreach (var f in styled)
        {
            long rank = WeightRank(want, f.Weight);
            if (rank < bestRank)
            {
                bestRank = rank;
                best = f;
            }
        }
        return new ResolvedFont(family, best.FilePath);
    }

    /// <summary>Every file registered under <paramref name="family"/> (empty when none is).</summary>
    internal static IReadOnlyList<string> ResolveFiles(string family)
    {
        if (string.IsNullOrWhiteSpace(family) || !_map.TryGetValue(family, out var faces))
            return [];
        return Array.ConvertAll(faces, f => f.FilePath);
    }

    // The CSS weight preference order as a rank (lower is preferred); distances fit in 1000, so a group offset of
    // 10000 keeps the groups apart.
    private static long WeightRank(int want, int have)
    {
        const long Second = 10_000, Third = 20_000;
        int d = Math.Abs(have - want);
        if (want >= 400 && want <= 500)
        {
            if (have >= want && have <= 500) return d;
            if (have < want) return Second + d;
            return Third + d;
        }
        if (want < 400)
            return have <= want ? d : Second + d;
        return have >= want ? d : Second + d;
    }
}
