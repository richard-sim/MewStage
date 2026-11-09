using System.Runtime.InteropServices;

using Aprillz.MewUI.Native.Structs;

namespace Aprillz.MewUI.Native;

internal static partial class Shell32
{
    private const string LibraryName = "shell32.dll";

    [DllImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DragAcceptFiles(nint hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAccept);

    [DllImport(LibraryName, EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    public static extern unsafe uint DragQueryFile(nint hDrop, uint iFile, char* lpszFile, uint cch);

    [DllImport(LibraryName)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern unsafe bool DragQueryPoint(nint hDrop, POINT* lppt);

    [DllImport(LibraryName)]
    public static extern void DragFinish(nint hDrop);
}
