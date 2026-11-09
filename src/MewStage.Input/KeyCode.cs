namespace MewStage.Input;

/// <summary>Device-agnostic key identifiers: the whole keyboard a UI needs (letters, digits, punctuation, editing,
/// navigation, function and modifier keys, the keypad). A key names a physical position in the US-QWERTY layout,
/// as the host's scancode map produces it; typed characters travel separately in
/// <see cref="RawInputSnapshot.TextInput"/>. New keys are appended so earlier values keep their numbers.</summary>
public enum KeyCode
{
    /// <summary>No key.</summary>
    None = 0,

    /// <summary>Left arrow.</summary>
    Left,

    /// <summary>Right arrow.</summary>
    Right,

    /// <summary>Up arrow.</summary>
    Up,

    /// <summary>Down arrow.</summary>
    Down,

    /// <summary>Space bar.</summary>
    Space,

    /// <summary>Comma.</summary>
    Comma,

    /// <summary>Period.</summary>
    Period,

    /// <summary>Enter / Return on the main block.</summary>
    Enter,

    /// <summary>Escape.</summary>
    Escape,

    /// <summary>Backtick / grave accent.</summary>
    Backtick,

    /// <summary>Tab.</summary>
    Tab,

    /// <summary>Backspace.</summary>
    Backspace,

    /// <summary>Delete.</summary>
    Delete,

    /// <summary>Home.</summary>
    Home,

    /// <summary>End.</summary>
    End,

    /// <summary>Left Shift.</summary>
    LeftShift,

    /// <summary>Right Shift.</summary>
    RightShift,

    /// <summary>Left Control.</summary>
    LeftCtrl,

    /// <summary>Right Control.</summary>
    RightCtrl,

    /// <summary>Left Alt.</summary>
    LeftAlt,

    /// <summary>Right Alt.</summary>
    RightAlt,

    /// <summary>W.</summary>
    W,

    /// <summary>A.</summary>
    A,

    /// <summary>S.</summary>
    S,

    /// <summary>D.</summary>
    D,

    /// <summary>Q.</summary>
    Q,

    /// <summary>E.</summary>
    E,

    /// <summary>R.</summary>
    R,

    /// <summary>F.</summary>
    F,

    /// <summary>F1.</summary>
    F1,

    /// <summary>F2.</summary>
    F2,

    /// <summary>F3.</summary>
    F3,

    /// <summary>F4.</summary>
    F4,

    /// <summary>F5.</summary>
    F5,

    /// <summary>F6.</summary>
    F6,

    /// <summary>F7.</summary>
    F7,

    /// <summary>F8.</summary>
    F8,

    /// <summary>F9.</summary>
    F9,

    /// <summary>F10.</summary>
    F10,

    /// <summary>F11.</summary>
    F11,

    /// <summary>F12.</summary>
    F12,

    /// <summary>Page Up.</summary>
    PageUp,

    /// <summary>Page Down.</summary>
    PageDown,

    /// <summary>B.</summary>
    B,

    /// <summary>C.</summary>
    C,

    /// <summary>G.</summary>
    G,

    /// <summary>H.</summary>
    H,

    /// <summary>I.</summary>
    I,

    /// <summary>J.</summary>
    J,

    /// <summary>K.</summary>
    K,

    /// <summary>L.</summary>
    L,

    /// <summary>M.</summary>
    M,

    /// <summary>N.</summary>
    N,

    /// <summary>O.</summary>
    O,

    /// <summary>P.</summary>
    P,

    /// <summary>T.</summary>
    T,

    /// <summary>U.</summary>
    U,

    /// <summary>V.</summary>
    V,

    /// <summary>X.</summary>
    X,

    /// <summary>Y.</summary>
    Y,

    /// <summary>Z.</summary>
    Z,

    /// <summary>0 on the main block.</summary>
    D0,

    /// <summary>1 on the main block.</summary>
    D1,

    /// <summary>2 on the main block.</summary>
    D2,

    /// <summary>3 on the main block.</summary>
    D3,

    /// <summary>4 on the main block.</summary>
    D4,

    /// <summary>5 on the main block.</summary>
    D5,

    /// <summary>6 on the main block.</summary>
    D6,

    /// <summary>7 on the main block.</summary>
    D7,

    /// <summary>8 on the main block.</summary>
    D8,

    /// <summary>9 on the main block.</summary>
    D9,

    /// <summary>Insert.</summary>
    Insert,

    /// <summary>Minus / hyphen.</summary>
    Minus,

    /// <summary>Equals.</summary>
    Equals,

    /// <summary>Left bracket.</summary>
    LeftBracket,

    /// <summary>Right bracket.</summary>
    RightBracket,

    /// <summary>Backslash.</summary>
    Backslash,

    /// <summary>Semicolon.</summary>
    Semicolon,

    /// <summary>Apostrophe.</summary>
    Apostrophe,

    /// <summary>Slash.</summary>
    Slash,

    /// <summary>Left GUI (Windows / Command).</summary>
    LeftGui,

    /// <summary>Right GUI (Windows / Command).</summary>
    RightGui,

    /// <summary>Menu / application key.</summary>
    Menu,

    /// <summary>Keypad 0.</summary>
    NumPad0,

    /// <summary>Keypad 1.</summary>
    NumPad1,

    /// <summary>Keypad 2.</summary>
    NumPad2,

    /// <summary>Keypad 3.</summary>
    NumPad3,

    /// <summary>Keypad 4.</summary>
    NumPad4,

    /// <summary>Keypad 5.</summary>
    NumPad5,

    /// <summary>Keypad 6.</summary>
    NumPad6,

    /// <summary>Keypad 7.</summary>
    NumPad7,

    /// <summary>Keypad 8.</summary>
    NumPad8,

    /// <summary>Keypad 9.</summary>
    NumPad9,

    /// <summary>Keypad plus.</summary>
    NumPadPlus,

    /// <summary>Keypad minus.</summary>
    NumPadMinus,

    /// <summary>Keypad multiply.</summary>
    NumPadMultiply,

    /// <summary>Keypad divide.</summary>
    NumPadDivide,

    /// <summary>Keypad period.</summary>
    NumPadPeriod,

    /// <summary>Keypad Enter.</summary>
    NumPadEnter,

    /// <summary>One past the last key; sizes the per-key arrays.</summary>
    Count,
}
