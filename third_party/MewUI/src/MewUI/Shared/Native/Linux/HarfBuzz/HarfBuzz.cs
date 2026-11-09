using System.Runtime.InteropServices;

namespace Aprillz.MewUI.Native.HarfBuzz;

internal static unsafe partial class HarfBuzz
{
    private const string LibraryName = "libharfbuzz.so.0";

    [LibraryImport(LibraryName)]
    public static partial nint hb_ft_font_create(nint ft_face, nint destroy);

    [LibraryImport(LibraryName)]
    public static partial void hb_font_destroy(nint font);

    [LibraryImport(LibraryName)]
    public static partial nint hb_buffer_create();

    [LibraryImport(LibraryName)]
    public static partial void hb_buffer_destroy(nint buffer);

    [LibraryImport(LibraryName)]
    public static partial void hb_buffer_reset(nint buffer);

    [LibraryImport(LibraryName)]
    public static partial void hb_buffer_add_utf16(nint buffer, ushort* text, int text_length, uint item_offset, int item_length);

    [LibraryImport(LibraryName)]
    public static partial void hb_buffer_set_direction(nint buffer, uint direction);

    [LibraryImport(LibraryName)]
    public static partial void hb_buffer_set_script(nint buffer, uint script);

    [LibraryImport(LibraryName)]
    public static partial void hb_buffer_set_language(nint buffer, nint language);

    [LibraryImport(LibraryName)]
    public static partial void hb_buffer_guess_segment_properties(nint buffer);

    [LibraryImport(LibraryName)]
    public static partial void hb_shape(nint font, nint buffer, nint features, uint num_features);

    [LibraryImport(LibraryName)]
    public static partial uint hb_buffer_get_length(nint buffer);

    [LibraryImport(LibraryName)]
    public static partial hb_glyph_info_t* hb_buffer_get_glyph_infos(nint buffer, out uint length);

    [LibraryImport(LibraryName)]
    public static partial hb_glyph_position_t* hb_buffer_get_glyph_positions(nint buffer, out uint length);

    [LibraryImport(LibraryName, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint hb_language_from_string(string str, int len);
}

[StructLayout(LayoutKind.Sequential)]
internal struct hb_glyph_info_t
{
    public uint codepoint;
    public uint mask;
    public uint cluster;
    private uint var1;
    private uint var2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct hb_glyph_position_t
{
    public int x_advance;
    public int y_advance;
    public int x_offset;
    public int y_offset;
    private uint var;
}

internal static class HbDirection
{
    public const uint LTR = 4;
    public const uint RTL = 5;
    public const uint TTB = 6;
    public const uint BTT = 7;
}
