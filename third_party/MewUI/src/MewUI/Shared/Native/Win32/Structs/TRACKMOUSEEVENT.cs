using System.Runtime.InteropServices;

namespace Aprillz.MewUI.Native.Structs;

[StructLayout(LayoutKind.Sequential)]
internal struct TRACKMOUSEEVENT
{
    public const uint TME_HOVER = 0x00000001;
    public const uint TME_LEAVE = 0x00000002;
    public const uint TME_NONCLIENT = 0x00000010;
    public const uint TME_QUERY = 0x40000000;
    public const uint TME_CANCEL = 0x80000000;

    public uint cbSize;
    public uint dwFlags;
    public nint hwndTrack;
    public uint dwHoverTime;
}
