namespace Aprillz.MewUI.Platform.Linux.X11;

/// <summary>
/// Represents the current preedit (composition) state from an input method.
/// </summary>
internal readonly struct X11PreeditState
{
    /// <summary>Current preedit text. Empty when composition ends.</summary>
    public string Text { get; init; }

    /// <summary>Per-character composition attributes. May be null.</summary>
    public CompositionAttr[]? Attributes { get; init; }

    /// <summary>Cursor position within the preedit string.</summary>
    public int CursorPos { get; init; }

    /// <summary>Whether this update signals the end of composition.</summary>
    public bool IsEnd { get; init; }
}
