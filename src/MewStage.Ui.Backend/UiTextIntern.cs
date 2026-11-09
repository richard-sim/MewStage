// Text reaches the bridge as ReadOnlySpan<char> (MewUI's IGraphicsContext text API), but FontStashSharp measures and
// draws a string, so a naive bridge would allocate `new string(span)` for EVERY measure and draw: hundreds of strings
// per frame under Continuous invalidation. UI text is overwhelmingly the same strings frame after frame, so the bridge
// looks the span up in an interning table (Dictionary alternate lookup by span: no allocation on a hit) and only
// allocates for text it has not seen. The table is per thread (the UI renders on one) and bounded: when it fills it is
// cleared, so a stream of ever-changing strings (a running clock) cannot grow it without limit.

namespace MewStage.Ui.Backend;

/// <summary>Span-to-string interning for the UI text bridge (no allocation for text seen before).</summary>
internal static class UiTextIntern
{
    /// <summary>Distinct strings kept before the table is cleared. A busy screen draws a few hundred distinct runs
    /// (every single character of letter-spaced text is one), so this holds several screens' worth.</summary>
    internal const int Capacity = 4096;

    [ThreadStatic] private static Dictionary<string, string>? s_table;

    /// <summary>The interned string equal to <paramref name="text"/>.</summary>
    public static string Get(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return string.Empty;
        }

        Dictionary<string, string> table = s_table ??= new Dictionary<string, string>(Capacity, StringComparer.Ordinal);
        Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> lookup = table.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(text, out string? s))
        {
            return s;
        }

        if (table.Count >= Capacity)
        {
            table.Clear();
        }

        s = new string(text);
        table[s] = s;
        return s;
    }
}
