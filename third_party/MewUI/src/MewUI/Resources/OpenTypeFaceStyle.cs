using System.Buffers.Binary;

namespace Aprillz.MewUI.Resources;

/// <summary>
/// MewStage vendor patch text/0003: the weight and style a font file's face declares, for <see cref="FontRegistry"/> to tell a
/// family's faces apart. The OS/2 table's usWeightClass and fsSelection (ITALIC bit 0, OBLIQUE bit 9) when it has one;
/// otherwise the head table's macStyle (Bold bit 0 = 700, Italic bit 1); otherwise a regular face (400, upright). A
/// collection (.ttc) answers for its first face.
/// </summary>
internal static class OpenTypeFaceStyle
{
    internal readonly record struct Style(int Weight, bool Italic);

    private static readonly Style Regular = new(400, false);

    private const uint TagOs2 = 0x4F532F32, TagHead = 0x68656164, TagTtcf = 0x74746366;   // 'OS/2', 'head', 'ttcf'
    private const int Os2WeightClass = 4, Os2FsSelection = 62, HeadMacStyle = 44;
    private const ushort FsItalic = 1 << 0, FsOblique = 1 << 9, MacBold = 1 << 0, MacItalic = 1 << 1;
    private const int BoldWeight = 700;

    public static Style Read(string path)
    {
        try
        {
            return Read(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return Regular;
        }
        catch (UnauthorizedAccessException)
        {
            return Regular;
        }
    }

    public static Style Read(ReadOnlySpan<byte> font)
    {
        int sfnt = 0;
        if (font.Length >= 16 && BinaryPrimitives.ReadUInt32BigEndian(font) == TagTtcf)
            sfnt = (int)BinaryPrimitives.ReadUInt32BigEndian(font[12..]);   // the first face's offset table

        if (sfnt < 0 || font.Length < sfnt + 12)
            return Regular;
        int numTables = BinaryPrimitives.ReadUInt16BigEndian(font[(sfnt + 4)..]);
        int os2 = -1, head = -1;
        for (int i = 0; i < numTables; i++)
        {
            int rec = sfnt + 12 + i * 16;
            if (font.Length < rec + 16)
                return Regular;
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(font[rec..]);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(font[(rec + 8)..]);
            if (tag == TagOs2) os2 = offset;
            else if (tag == TagHead) head = offset;
        }

        if (os2 >= 0 && font.Length >= os2 + Os2FsSelection + 2)
        {
            int weight = BinaryPrimitives.ReadUInt16BigEndian(font[(os2 + Os2WeightClass)..]);
            ushort fs = BinaryPrimitives.ReadUInt16BigEndian(font[(os2 + Os2FsSelection)..]);
            return new Style(weight is >= 1 and <= 1000 ? weight : Regular.Weight, (fs & (FsItalic | FsOblique)) != 0);
        }
        if (head >= 0 && font.Length >= head + HeadMacStyle + 2)
        {
            ushort mac = BinaryPrimitives.ReadUInt16BigEndian(font[(head + HeadMacStyle)..]);
            return new Style((mac & MacBold) != 0 ? BoldWeight : Regular.Weight, (mac & MacItalic) != 0);
        }
        return Regular;
    }
}
