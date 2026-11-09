// SdlInputBridge: feeds the device-agnostic RawInputSnapshot (filled by the SDL3 host while pumping events) into a
// MewUI Window via WindowInputRouter. The snapshot is LEVEL state (is-down) + per-frame mouse deltas; the router wants
// edge events (KeyDown / KeyUp, mouse button down / up) + absolute moves, so the bridge keeps a one-frame shadow of key
// and button state and emits a router call ONLY on a transition (or on a mouse move / non-zero wheel). MewUI's
// KeyEventArgs / MouseEventArgs are constructor-immutable (only Handled is settable), so a true zero-alloc pool is not
// possible; the bridge instead constructs an args object only on an actual edge, which in steady state (no input
// changing) is zero. The KeyCode -> Key and MouseButton mapping tables are static (no per-call allocation).

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;

using MewStage.Input;
using MewStage.Ui.Navigation;

using MewKey = Aprillz.MewUI.Key;
using MewMouseButton = Aprillz.MewUI.MouseButton;
using VKeyCode = MewStage.Input.KeyCode;
using VMouseButton = MewStage.Input.MouseButton;

namespace MewStage.Ui.Backend;

/// <summary>Routes a RawInputSnapshot frame into a MewUI Window (edge-diffed; alloc-minimal).</summary>
public sealed class SdlInputBridge
{
    private readonly bool[] _prevKey = new bool[(int)VKeyCode.Count];
    private readonly bool[] _prevMouse = new bool[(int)VMouseButton.Count];
    private float _prevMouseX, _prevMouseY;
    private bool _hasPrevMouse;
    // The text of a keystroke whose KeyDown was handled is dropped (MewUI's platform law,
    // Aprillz.MewUI.Input.TextInputSuppression, as Win32WindowBackend applies it to WM_CHAR). Per keystroke: the next
    // key-down edge that nobody handled ends it, so key-repeat text of a consumed key stays dropped too; releasing
    // the last held key ends it as well, so an IME commit (text with no key-down of its own) after a handled Escape
    // still types.
    private bool _suppressText;

    /// <summary>The game's hover = focus rule (a moved cursor focuses the control under it; FocusUnderCursor). True by
    /// default (a game-style overlay). A desktop-style workbench turns it OFF: hover gives a viewport area focus but
    /// must never take keyboard focus from a text field.</summary>
    public bool FocusFollowsMouse { get; set; } = true;

    /// <summary>Tab / Shift+Tab move keyboard focus when no element handled the key - what MewUI's PLATFORM backends do
    /// (Win32WindowBackend "WPF-like Tab behavior": the focused element sees KeyDown first, focus moves only if it is
    /// still unhandled), which the embedded host otherwise lacks. Off by default so a game-style overlay keeps its own focus
    /// model (the gamepad navigator); a desktop-style workbench turns it on.</summary>
    public bool TabNavigation { get; set; }

    /// <summary>
    /// Pixels-&gt;DIP divisor for mouse coordinates. RawInputSnapshot mouse X/Y arrive in window PIXELS, but MewUI
    /// lays out and hit-tests in DIP (the host's <c>EmbeddedUiHost.Resize</c> calls
    /// <c>Window.SetClientSizeDip(pixelW / dpiScale, ...)</c>). Feeding raw pixels into
    /// <see cref="WindowInputRouter"/> therefore offsets/scales every click by the DPI factor (the ~40–50px miss at
    /// 125–150% scaling). The host sets this to the SAME <c>dpiScale</c> it passes to Resize, every frame, so a
    /// move to a different-DPI monitor stays correct. Clamped to ≥ a tiny epsilon; default 1.0 (no HiDPI = no-op).
    /// </summary>
    public float Scale
    {
        get;
        set => field = value > 1e-4f ? value : 1f;
    } = 1f;

    /// <summary>
    /// HOVER = FOCUS: moving the mouse over a
    /// focusable element gives it the focus, so a screen shows exactly one highlight whichever device the user uses.
    /// Only a MOVE does it (a still cursor never takes the focus from a screen that just opened, or from the pad), never
    /// while a button is held (a slider drag). The element under the cursor or its nearest focusable, enabled, visible
    /// ancestor takes it; moving over nothing focusable takes the focus away (no highlight is left
    /// behind) and parks it for the pad / keyboard to bring back (MewStage.Ui.Navigation.HoverPark).
    /// </summary>
    public static void FocusUnderCursor(Window window, Point positionInWindow)
    {
        ArgumentNullException.ThrowIfNull(window);
        FocusManager fm = window.FocusManager;
        for (Element? e = WindowInputRouter.HitTest(window, positionInWindow); e is not null; e = e.Parent)
        {
            if (e is not UIElement u || !u.Focusable || !u.IsEffectivelyEnabled || !u.IsVisible)
            {
                continue;
            }

            if (!ReferenceEquals(fm.FocusedElement, u))
            {
                fm.SetFocus(u);
            }

            HoverPark.Clear(window);   // the mouse is on a control: nothing is parked
            return;
        }
        // Nothing focusable under the cursor: the mouse LEFT the control (the highlight must not
        // stay behind). Take the focus away and park it (HoverPark: the next pad press or Tab / Enter brings it back) -
        // except from a value control that is engaged or has its list open, which the user is still using.
        if (fm.FocusedElement is not { } focused)
        {
            return;
        }

        if (focused is IGamepadValueControl { IsGamepadEngaged: true })
        {
            return;
        }

        HoverPark.Park(window, focused);
        fm.ClearFocus();
    }

    /// <summary>MewUI wheel delta from SDL wheel values: MewUI's +X is scroll LEFT (WindowInputRouter.MouseWheel
    /// doc), SDL's +x is scroll RIGHT (SDL_MouseWheelEvent), so X is negated; +Y is up in both.</summary>
    public static Vector WheelDelta(float sdlX, float sdlY)
    {
        return new(-sdlX, sdlY);
    }

    /// <summary>
    /// Apply one frame of input to <paramref name="window"/>. Emits MewUI router calls only for transitions
    /// (key/button down↔up), absolute mouse moves (when the cursor moved), and non-zero wheel deltas.
    /// </summary>
    public void Dispatch(Window window, RawInputSnapshot input)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(input);
        if (window.Handle == 0)
        {
            return;
        }

        ModifierKeys modifiers = ResolveModifiers(input);
        float inv = 1f / Scale;
        var pos = new Point(input.MouseX * inv, input.MouseY * inv);
        bool left = input.IsMouseDown(VMouseButton.Left);
        bool right = input.IsMouseDown(VMouseButton.Right);
        bool middle = input.IsMouseDown(VMouseButton.Middle);

        // --- mouse move (only when the cursor actually moved) ---
        if (!_hasPrevMouse || input.MouseX != _prevMouseX || input.MouseY != _prevMouseY)
        {
            bool moved = _hasPrevMouse;   // the first frame only places the cursor: it must not steal a screen's initial focus
            WindowInputRouter.MouseMove(window, pos, pos, left, right, middle, modifiers);
            _prevMouseX = input.MouseX;
            _prevMouseY = input.MouseY;
            _hasPrevMouse = true;
            if (moved && !left && !right && !middle && FocusFollowsMouse)
            {
                FocusUnderCursor(window, pos);
            }
        }

        // --- mouse buttons (edge-diffed) ---
        // The held modifiers go WITH every mouse event. MewUI's mouse args carry them only when the host
        // passes them (the platform backends read the OS state); the embedded host never sends modifier KEY events
        // (MapKey: None), so without this a Shift+MMB press arrived as plain MMB (orbit instead of pan).
        DispatchButton(window, input, pos, VMouseButton.Left, MewMouseButton.Left, left, right, middle, modifiers);
        DispatchButton(window, input, pos, VMouseButton.Right, MewMouseButton.Right, left, right, middle, modifiers);
        DispatchButton(window, input, pos, VMouseButton.Middle, MewMouseButton.Middle, left, right, middle, modifiers);

        // --- wheel (both axes from the snapshot; MouseWheelX is 0 on hosts that do not pump it) ---
        if (input.MouseWheel != 0f || input.MouseWheelX != 0f)
        {
            WindowInputRouter.MouseWheel(window, pos, pos, WheelDelta(input.MouseWheelX, input.MouseWheel), left, right, middle, modifiers);
        }

        // --- keys (edge-diffed; see DispatchKeys) ---
        DispatchKeys(window, input, modifiers);

        // --- typed text (after the keys, as the OS orders KEY_DOWN before its TEXT_INPUT) ---
        // Delivered to the window (its preview, then the focused text client) unless another layer owns the keyboard
        // this frame (UiWantsKeyboard set by the application). Not delivered either when a handled key-down produced it
        // (a shortcut key must not also type its letter into a text box). Consumed in every case, so a frame that is pumped twice never types twice. A string is allocated only when
        // text was typed (never in a steady-state frame).
        if (input.TextInputLength > 0)
        {
            if (!input.UiWantsKeyboard && !_suppressText)
            {
                WindowInputRouter.TextInput(window, new TextInputEventArgs(new string(input.TextInput, 0, input.TextInputLength)));
            }

            input.TextInputLength = 0;
        }
    }

    // The snapshot does not order a frame's key-downs against its text, so a frame with any handled key-down drops
    // the frame's text (SDL queues a keystroke's TEXT_INPUT right after its KEY_DOWN, in the same pump); a frame
    // whose key-downs were all unhandled ends the suppression. Modifier keys are never key events (MapKey: None), so a
    // Shift pressed between a consumed key and its text leaves the suppression as it is.
    private void DispatchKeys(Window window, RawInputSnapshot input, ModifierKeys modifiers)
    {
        bool anyDown = false, anyHandled = false, anyUp = false;
        for (int i = 1; i < (int)VKeyCode.Count; i++)
        {
            bool down = input.KeyDown[i];
            if (down == _prevKey[i])
            {
                continue;
            }

            _prevKey[i] = down;
            MewKey key = MapKey((VKeyCode)i);
            if (key == MewKey.None)
            {
                continue;
            }

            var args = new KeyEventArgs(key, i, modifiers);
            if (down)
            {
                WindowInputRouter.KeyDown(window, args);
                if (TabNavigation && !args.Handled && key == MewKey.Tab
                    && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Meta)) == 0)
                {
                    if ((modifiers & ModifierKeys.Shift) != 0)
                    {
                        window.FocusManager.MoveFocusPrevious();
                    }
                    else
                    {
                        window.FocusManager.MoveFocusNext();
                    }

                    args.Handled = true;   // as Win32WindowBackend: the Tab moved focus, its text is not typed
                }

                anyDown = true;
                anyHandled |= args.Handled;
            }
            else
            {
                WindowInputRouter.KeyUp(window, args);
                anyUp = true;
            }
        }

        if (anyDown)
        {
            _suppressText = anyHandled;
        }
        else if (anyUp && _suppressText && !AnyKeyHeld(input))
        {
            _suppressText = false;
        }
    }

    private static bool AnyKeyHeld(RawInputSnapshot input)
    {
        bool[] keys = input.KeyDown;
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i])
            {
                return true;
            }
        }

        return false;
    }

    private void DispatchButton(Window window, RawInputSnapshot input, Point pos, VMouseButton vb, MewMouseButton mb,
        bool left, bool right, bool middle, ModifierKeys modifiers)
    {
        bool down = input.IsMouseDown(vb);
        if (down == _prevMouse[(int)vb])
        {
            return;
        }

        _prevMouse[(int)vb] = down;
        WindowInputRouter.MouseButton(window, pos, pos, mb, down, left, right, middle, down ? CountClick(vb, pos) : _clickCount[(int)vb], modifiers);
    }

    // Click counting (a double-click is two presses of one button close in time and space). SDL's own defaults
    // (SDL_HINT_MOUSE_DOUBLE_CLICK_TIME 500 ms, SDL_HINT_MOUSE_DOUBLE_CLICK_RADIUS 32 px); the press carries the count and its release repeats it.
    /// <summary>Two presses of one button within this many seconds (and <see cref="DoubleClickRadius"/>) count as a
    /// double-click; SDL's own default. Public so a test harness can space its single clicks apart.</summary>
    public const double DoubleClickSeconds = 0.5;

    /// <summary>The radius in DIPs within which a second press continues a click sequence; SDL's own default.</summary>
    public const double DoubleClickRadius = 32;
    private readonly int[] _clickCount = new int[(int)VMouseButton.Count];
    private readonly long[] _lastPressTicks = new long[(int)VMouseButton.Count];
    private readonly Point[] _lastPressPos = new Point[(int)VMouseButton.Count];

    /// <summary>The clock of the click counter (Stopwatch ticks; null = the real clock). Tests that script a double-click set it.</summary>
    public Func<long>? Timestamp { get; set; }

    /// <summary>The click count the most recent press reported (1 = single, 2 = double ...); test introspection.</summary>
    public int LastClickCount { get; private set; }

    private int CountClick(VMouseButton vb, Point pos)
    {
        int i = (int)vb;
        long now = Timestamp?.Invoke() ?? System.Diagnostics.Stopwatch.GetTimestamp();
        bool again = _clickCount[i] > 0
            && Math.Abs(now - _lastPressTicks[i]) <= (long)(DoubleClickSeconds * System.Diagnostics.Stopwatch.Frequency)
            && Math.Abs(pos.X - _lastPressPos[i].X) <= DoubleClickRadius && Math.Abs(pos.Y - _lastPressPos[i].Y) <= DoubleClickRadius;
        _clickCount[i] = again ? _clickCount[i] + 1 : 1;
        _lastPressTicks[i] = now;
        _lastPressPos[i] = pos;
        LastClickCount = _clickCount[i];
        return _clickCount[i];
    }

    private static ModifierKeys ResolveModifiers(RawInputSnapshot input)
    {
        ModifierKeys m = ModifierKeys.None;
        if (input.IsKeyDown(VKeyCode.LeftCtrl) || input.IsKeyDown(VKeyCode.RightCtrl))
        {
            m |= ModifierKeys.Control;
        }

        if (input.IsKeyDown(VKeyCode.LeftShift) || input.IsKeyDown(VKeyCode.RightShift))
        {
            m |= ModifierKeys.Shift;
        }

        if (input.IsKeyDown(VKeyCode.LeftAlt) || input.IsKeyDown(VKeyCode.RightAlt))
        {
            m |= ModifierKeys.Alt;
        }

        if (input.IsKeyDown(VKeyCode.LeftGui) || input.IsKeyDown(VKeyCode.RightGui))
        {
            m |= ModifierKeys.Meta;
        }

        return m;
    }

    /// <summary>The MewUI key for a MewStage key (None for the modifiers, which arrive as <see cref="ModifierKeys"/>).
    /// Every non-modifier <see cref="VKeyCode"/> has one (pinned by the Host tests).</summary>
    internal static MewKey MapKey(VKeyCode k)
    {
        return k switch
        {
            VKeyCode.Left => MewKey.Left,
            VKeyCode.Right => MewKey.Right,
            VKeyCode.Up => MewKey.Up,
            VKeyCode.Down => MewKey.Down,
            VKeyCode.Space => MewKey.Space,
            VKeyCode.Enter => MewKey.Enter,
            VKeyCode.Escape => MewKey.Escape,
            VKeyCode.Tab => MewKey.Tab,
            VKeyCode.Backspace => MewKey.Backspace,
            VKeyCode.Delete => MewKey.Delete,
            VKeyCode.Home => MewKey.Home,
            VKeyCode.End => MewKey.End,
            VKeyCode.Comma => MewKey.Comma,
            VKeyCode.Period => MewKey.Period,
            VKeyCode.Backtick => MewKey.Backquote,
            VKeyCode.A => MewKey.A,
            VKeyCode.B => MewKey.B,
            VKeyCode.C => MewKey.C,
            VKeyCode.D => MewKey.D,
            VKeyCode.E => MewKey.E,
            VKeyCode.F => MewKey.F,
            VKeyCode.G => MewKey.G,
            VKeyCode.H => MewKey.H,
            VKeyCode.I => MewKey.I,
            VKeyCode.J => MewKey.J,
            VKeyCode.K => MewKey.K,
            VKeyCode.L => MewKey.L,
            VKeyCode.M => MewKey.M,
            VKeyCode.N => MewKey.N,
            VKeyCode.O => MewKey.O,
            VKeyCode.P => MewKey.P,
            VKeyCode.Q => MewKey.Q,
            VKeyCode.R => MewKey.R,
            VKeyCode.S => MewKey.S,
            VKeyCode.T => MewKey.T,
            VKeyCode.U => MewKey.U,
            VKeyCode.V => MewKey.V,
            VKeyCode.W => MewKey.W,
            VKeyCode.X => MewKey.X,
            VKeyCode.Y => MewKey.Y,
            VKeyCode.Z => MewKey.Z,
            VKeyCode.D0 => MewKey.D0,
            VKeyCode.D1 => MewKey.D1,
            VKeyCode.D2 => MewKey.D2,
            VKeyCode.D3 => MewKey.D3,
            VKeyCode.D4 => MewKey.D4,
            VKeyCode.D5 => MewKey.D5,
            VKeyCode.D6 => MewKey.D6,
            VKeyCode.D7 => MewKey.D7,
            VKeyCode.D8 => MewKey.D8,
            VKeyCode.D9 => MewKey.D9,
            VKeyCode.Insert => MewKey.Insert,
            VKeyCode.Minus => MewKey.Minus,
            VKeyCode.Equals => MewKey.Equal,
            VKeyCode.LeftBracket => MewKey.BracketLeft,
            VKeyCode.RightBracket => MewKey.BracketRight,
            VKeyCode.Backslash => MewKey.Backslash,
            VKeyCode.Semicolon => MewKey.Semicolon,
            VKeyCode.Apostrophe => MewKey.Quote,
            VKeyCode.Slash => MewKey.Slash,
            VKeyCode.Menu => MewKey.ContextMenu,
            VKeyCode.F1 => MewKey.F1,
            VKeyCode.F2 => MewKey.F2,
            VKeyCode.F3 => MewKey.F3,
            VKeyCode.F4 => MewKey.F4,
            VKeyCode.F5 => MewKey.F5,
            VKeyCode.F6 => MewKey.F6,
            VKeyCode.F7 => MewKey.F7,
            VKeyCode.F8 => MewKey.F8,
            VKeyCode.F9 => MewKey.F9,
            VKeyCode.F10 => MewKey.F10,
            VKeyCode.F11 => MewKey.F11,
            VKeyCode.F12 => MewKey.F12,
            VKeyCode.PageUp => MewKey.PageUp,
            VKeyCode.PageDown => MewKey.PageDown,
            VKeyCode.NumPad0 => MewKey.NumPad0,
            VKeyCode.NumPad1 => MewKey.NumPad1,
            VKeyCode.NumPad2 => MewKey.NumPad2,
            VKeyCode.NumPad3 => MewKey.NumPad3,
            VKeyCode.NumPad4 => MewKey.NumPad4,
            VKeyCode.NumPad5 => MewKey.NumPad5,
            VKeyCode.NumPad6 => MewKey.NumPad6,
            VKeyCode.NumPad7 => MewKey.NumPad7,
            VKeyCode.NumPad8 => MewKey.NumPad8,
            VKeyCode.NumPad9 => MewKey.NumPad9,
            VKeyCode.NumPadPlus => MewKey.Add,
            VKeyCode.NumPadMinus => MewKey.Subtract,
            VKeyCode.NumPadMultiply => MewKey.Multiply,
            VKeyCode.NumPadDivide => MewKey.Divide,
            VKeyCode.NumPadPeriod => MewKey.Decimal,
            VKeyCode.NumPadEnter => MewKey.Enter,
            _ => MewKey.None,
        };
    }

    /// <summary>The modifier keys (reported through <see cref="ModifierKeys"/>, never as key events).</summary>
    internal static bool IsModifier(VKeyCode k)
    {
        return k is VKeyCode.LeftShift or VKeyCode.RightShift or VKeyCode.LeftCtrl
        or VKeyCode.RightCtrl or VKeyCode.LeftAlt or VKeyCode.RightAlt or VKeyCode.LeftGui or VKeyCode.RightGui;
    }
}
