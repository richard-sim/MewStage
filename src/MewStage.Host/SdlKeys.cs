// SDL keyboard and text input into the device-agnostic RawInputSnapshot (MewStage.Input never references SDL). The
// scancode map covers the whole keyboard, so MewUI gets letters, digits and punctuation (DevTools shortcuts, text
// editing) as well as the navigation keys.
using System;
using System.Text;

using MewStage.Input;

using SDL;

namespace MewStage.Host;

/// <summary>SDL scancode and text-input translation.</summary>
public static unsafe class SdlKeys
{
    // UTF-8 bytes one SDL text event may carry before it is cut (SDL's own events are a few code points).
    private const int MaxEventBytes = 256;

    /// <summary>The <see cref="KeyCode"/> for an SDL scancode (a physical key position), or None.</summary>
    public static KeyCode MapScancode(SDL_Scancode s)
    {
        return s switch
        {
            SDL_Scancode.SDL_SCANCODE_LEFT => KeyCode.Left,
            SDL_Scancode.SDL_SCANCODE_RIGHT => KeyCode.Right,
            SDL_Scancode.SDL_SCANCODE_UP => KeyCode.Up,
            SDL_Scancode.SDL_SCANCODE_DOWN => KeyCode.Down,
            SDL_Scancode.SDL_SCANCODE_SPACE => KeyCode.Space,
            SDL_Scancode.SDL_SCANCODE_COMMA => KeyCode.Comma,
            SDL_Scancode.SDL_SCANCODE_PERIOD => KeyCode.Period,
            SDL_Scancode.SDL_SCANCODE_RETURN => KeyCode.Enter,
            SDL_Scancode.SDL_SCANCODE_ESCAPE => KeyCode.Escape,
            SDL_Scancode.SDL_SCANCODE_GRAVE => KeyCode.Backtick,
            SDL_Scancode.SDL_SCANCODE_TAB => KeyCode.Tab,
            SDL_Scancode.SDL_SCANCODE_BACKSPACE => KeyCode.Backspace,
            SDL_Scancode.SDL_SCANCODE_DELETE => KeyCode.Delete,
            SDL_Scancode.SDL_SCANCODE_HOME => KeyCode.Home,
            SDL_Scancode.SDL_SCANCODE_END => KeyCode.End,
            SDL_Scancode.SDL_SCANCODE_LSHIFT => KeyCode.LeftShift,
            SDL_Scancode.SDL_SCANCODE_RSHIFT => KeyCode.RightShift,
            SDL_Scancode.SDL_SCANCODE_LCTRL => KeyCode.LeftCtrl,
            SDL_Scancode.SDL_SCANCODE_RCTRL => KeyCode.RightCtrl,
            SDL_Scancode.SDL_SCANCODE_LALT => KeyCode.LeftAlt,
            SDL_Scancode.SDL_SCANCODE_RALT => KeyCode.RightAlt,
            SDL_Scancode.SDL_SCANCODE_LGUI => KeyCode.LeftGui,
            SDL_Scancode.SDL_SCANCODE_RGUI => KeyCode.RightGui,
            SDL_Scancode.SDL_SCANCODE_APPLICATION => KeyCode.Menu,
            SDL_Scancode.SDL_SCANCODE_A => KeyCode.A,
            SDL_Scancode.SDL_SCANCODE_B => KeyCode.B,
            SDL_Scancode.SDL_SCANCODE_C => KeyCode.C,
            SDL_Scancode.SDL_SCANCODE_D => KeyCode.D,
            SDL_Scancode.SDL_SCANCODE_E => KeyCode.E,
            SDL_Scancode.SDL_SCANCODE_F => KeyCode.F,
            SDL_Scancode.SDL_SCANCODE_G => KeyCode.G,
            SDL_Scancode.SDL_SCANCODE_H => KeyCode.H,
            SDL_Scancode.SDL_SCANCODE_I => KeyCode.I,
            SDL_Scancode.SDL_SCANCODE_J => KeyCode.J,
            SDL_Scancode.SDL_SCANCODE_K => KeyCode.K,
            SDL_Scancode.SDL_SCANCODE_L => KeyCode.L,
            SDL_Scancode.SDL_SCANCODE_M => KeyCode.M,
            SDL_Scancode.SDL_SCANCODE_N => KeyCode.N,
            SDL_Scancode.SDL_SCANCODE_O => KeyCode.O,
            SDL_Scancode.SDL_SCANCODE_P => KeyCode.P,
            SDL_Scancode.SDL_SCANCODE_Q => KeyCode.Q,
            SDL_Scancode.SDL_SCANCODE_R => KeyCode.R,
            SDL_Scancode.SDL_SCANCODE_S => KeyCode.S,
            SDL_Scancode.SDL_SCANCODE_T => KeyCode.T,
            SDL_Scancode.SDL_SCANCODE_U => KeyCode.U,
            SDL_Scancode.SDL_SCANCODE_V => KeyCode.V,
            SDL_Scancode.SDL_SCANCODE_W => KeyCode.W,
            SDL_Scancode.SDL_SCANCODE_X => KeyCode.X,
            SDL_Scancode.SDL_SCANCODE_Y => KeyCode.Y,
            SDL_Scancode.SDL_SCANCODE_Z => KeyCode.Z,
            SDL_Scancode.SDL_SCANCODE_0 => KeyCode.D0,
            SDL_Scancode.SDL_SCANCODE_1 => KeyCode.D1,
            SDL_Scancode.SDL_SCANCODE_2 => KeyCode.D2,
            SDL_Scancode.SDL_SCANCODE_3 => KeyCode.D3,
            SDL_Scancode.SDL_SCANCODE_4 => KeyCode.D4,
            SDL_Scancode.SDL_SCANCODE_5 => KeyCode.D5,
            SDL_Scancode.SDL_SCANCODE_6 => KeyCode.D6,
            SDL_Scancode.SDL_SCANCODE_7 => KeyCode.D7,
            SDL_Scancode.SDL_SCANCODE_8 => KeyCode.D8,
            SDL_Scancode.SDL_SCANCODE_9 => KeyCode.D9,
            SDL_Scancode.SDL_SCANCODE_INSERT => KeyCode.Insert,
            SDL_Scancode.SDL_SCANCODE_MINUS => KeyCode.Minus,
            SDL_Scancode.SDL_SCANCODE_EQUALS => KeyCode.Equals,
            SDL_Scancode.SDL_SCANCODE_LEFTBRACKET => KeyCode.LeftBracket,
            SDL_Scancode.SDL_SCANCODE_RIGHTBRACKET => KeyCode.RightBracket,
            SDL_Scancode.SDL_SCANCODE_BACKSLASH => KeyCode.Backslash,
            SDL_Scancode.SDL_SCANCODE_SEMICOLON => KeyCode.Semicolon,
            SDL_Scancode.SDL_SCANCODE_APOSTROPHE => KeyCode.Apostrophe,
            SDL_Scancode.SDL_SCANCODE_SLASH => KeyCode.Slash,
            SDL_Scancode.SDL_SCANCODE_F1 => KeyCode.F1,
            SDL_Scancode.SDL_SCANCODE_F2 => KeyCode.F2,
            SDL_Scancode.SDL_SCANCODE_F3 => KeyCode.F3,
            SDL_Scancode.SDL_SCANCODE_F4 => KeyCode.F4,
            SDL_Scancode.SDL_SCANCODE_F5 => KeyCode.F5,
            SDL_Scancode.SDL_SCANCODE_F6 => KeyCode.F6,
            SDL_Scancode.SDL_SCANCODE_F7 => KeyCode.F7,
            SDL_Scancode.SDL_SCANCODE_F8 => KeyCode.F8,
            SDL_Scancode.SDL_SCANCODE_F9 => KeyCode.F9,
            SDL_Scancode.SDL_SCANCODE_F10 => KeyCode.F10,
            SDL_Scancode.SDL_SCANCODE_F11 => KeyCode.F11,
            SDL_Scancode.SDL_SCANCODE_F12 => KeyCode.F12,
            SDL_Scancode.SDL_SCANCODE_PAGEUP => KeyCode.PageUp,
            SDL_Scancode.SDL_SCANCODE_PAGEDOWN => KeyCode.PageDown,
            SDL_Scancode.SDL_SCANCODE_KP_0 => KeyCode.NumPad0,
            SDL_Scancode.SDL_SCANCODE_KP_1 => KeyCode.NumPad1,
            SDL_Scancode.SDL_SCANCODE_KP_2 => KeyCode.NumPad2,
            SDL_Scancode.SDL_SCANCODE_KP_3 => KeyCode.NumPad3,
            SDL_Scancode.SDL_SCANCODE_KP_4 => KeyCode.NumPad4,
            SDL_Scancode.SDL_SCANCODE_KP_5 => KeyCode.NumPad5,
            SDL_Scancode.SDL_SCANCODE_KP_6 => KeyCode.NumPad6,
            SDL_Scancode.SDL_SCANCODE_KP_7 => KeyCode.NumPad7,
            SDL_Scancode.SDL_SCANCODE_KP_8 => KeyCode.NumPad8,
            SDL_Scancode.SDL_SCANCODE_KP_9 => KeyCode.NumPad9,
            SDL_Scancode.SDL_SCANCODE_KP_PLUS => KeyCode.NumPadPlus,
            SDL_Scancode.SDL_SCANCODE_KP_MINUS => KeyCode.NumPadMinus,
            SDL_Scancode.SDL_SCANCODE_KP_MULTIPLY => KeyCode.NumPadMultiply,
            SDL_Scancode.SDL_SCANCODE_KP_DIVIDE => KeyCode.NumPadDivide,
            SDL_Scancode.SDL_SCANCODE_KP_PERIOD => KeyCode.NumPadPeriod,
            SDL_Scancode.SDL_SCANCODE_KP_ENTER => KeyCode.NumPadEnter,
            _ => KeyCode.None,
        };
    }

    /// <summary>
    /// Append an SDL_EVENT_TEXT_INPUT's NUL-terminated UTF-8 text to <paramref name="input"/>'s typed text.
    /// Allocation-free (decodes into a stack buffer).
    /// </summary>
    public static void AppendText(byte* utf8, RawInputSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (utf8 == null)
        {
            return;
        }

        int n = 0;
        while (n < MaxEventBytes && utf8[n] != 0)
        {
            n++;
        }

        if (n == 0)
        {
            return;
        }

        var bytes = new ReadOnlySpan<byte>(utf8, n);
        Span<char> chars = stackalloc char[MaxEventBytes];   // UTF-8 never decodes to more chars than bytes
        int count = Encoding.UTF8.GetChars(bytes, chars);
        input.AppendTextInput(chars[..count]);
    }
}
