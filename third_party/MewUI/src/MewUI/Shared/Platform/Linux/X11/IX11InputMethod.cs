using Aprillz.MewUI.Native;

namespace Aprillz.MewUI.Platform.Linux.X11;

/// <summary>
/// Abstracts an X11 input method backend (IBus, fcitx5, or XIM fallback).
/// <see cref="X11WindowBackend"/> delegates all IME interactions through this interface.
/// </summary>
internal interface IX11InputMethod : IDisposable
{
    bool IsAvailable { get; }
    bool SupportsPreedit { get; }

    void OnFocusIn();
    void OnFocusOut();
    void Reset();
    void UpdateCursorRect(Rect rectInWindowPx);

    X11ImeProcessResult ProcessKeyEvent(ref XEvent ev, bool isKeyDown);

    event Action<string>? CommitText;
    event Action<X11PreeditState>? PreeditChanged;
}
