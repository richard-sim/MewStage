// GamepadFocusNavigator: console-style spatial / gamepad focus navigation over a MewUI Window's FocusManager. MewUI
// ships only linear Tab / Shift+Tab (MoveFocusNext / Previous); this layers a 2-D directional model on top:
//
//   * Direction comes from BOTH the left stick AND the D-pad, aggregated across EVERY connected gamepad-kind device
//     ("whoever picks up a pad drives the menu"); devices that are not gamepads are skipped so they cannot hijack it.
//   * A held direction fires once immediately, then auto-repeats after an initial delay at a fixed interval, so a long
//     list can be scanned by holding.
//   * The neighbor pick is an overlap-aware angular score (WinUI XYFocus / Chromium FocusController style), extracted
//     into the PURE, unit-testable static PickNeighbor over synthetic rects.
//   * Edges WRAP and a freshly-entered window is GUARANTEED an initial focus cursor; focus is scoped to the single TOP
//     window the host feeds in (the modal trap).
//   * Cross-axis (column / row) memory keeps your column on vertical moves and your row on horizontal moves.
//   * Value controls (Slider / RangeBase, ComboBox / DropDownBase, and any IGamepadValueControl) use an ENGAGE model:
//     A / Enter engages, Left / Right (with repeat) adjust the value / cycle items, B (or A) disengages; plain controls
//     (Button) activate on A / Enter.
//   * A routes Enter (activate) to the focused element; B / Back route Escape to it, which falls through to the window's
//     input map, where Escape is the screen's Back command (NavCommands): the pad's B and the keyboard's Escape are one
//     path.
//   * The screen-level buttons run the navigation commands (NavCommands) in the window: X / Y ActionX / ActionY, LB / RB
//     PrevTab / NextTab, Start Confirm (the screen's primary action; with no Confirm handler Start activates the focused
//     element like A). On a window with nothing focusable, A runs Confirm and Left / Right run CycleLeft / CycleRight (a
//     screen driven by its legend alone).
//   * The mouse moving onto empty space clears the focus and parks it (HoverPark); the next direction or A only brings
//     that highlight back.
//   * A focused element that is hidden or disabled hands the focus to the screen's fallback (FocusFallback; its primary
//     button, else Back), and gets it back when it returns if the user has not moved since (RepairFocus).
//   * NO focus ring is drawn: every focusable control draws its own focused and ENGAGED state through its style states.
//     A fire-and-forget feedback callback is raised for an optional audio cue.
//
// The navigator runs at frame rate and routes ONLY MewUI key / focus events to the focused widget. Steady state is
// allocation-free (timers, state and reused buffers are fields).

using System.Runtime.InteropServices;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;

using MewStage.Input;
using MewStage.Ui.Navigation;

using MewKey = Aprillz.MewUI.Key;

namespace MewStage.Ui.Backend;

/// <summary>What the gamepad navigator just did, for an optional feedback cue.</summary>
public enum UiNavFeedbackKind
{
    /// <summary>The focus moved to another element.</summary>
    Move,

    /// <summary>The focused element was activated, or a navigation command ran.</summary>
    Activate,

    /// <summary>A value control was engaged.</summary>
    Engage,

    /// <summary>An engaged value control's value changed.</summary>
    Adjust,

    /// <summary>An engaged value control was released.</summary>
    Disengage,
}

/// <summary>Console-style spatial gamepad focus navigation over a MewUI Window's FocusManager.</summary>
public sealed class GamepadFocusNavigator : IDisposable
{
    private const float StickThreshold = 0.5f;
    // Hold-to-repeat: fire on press, wait this long, then repeat at the interval while held.
    private const double RepeatInitialDelay = 0.40;   // seconds before auto-repeat begins
    private const double RepeatInterval = 0.12;        // seconds between repeats while held
    // Directional scoring weights. Lower score wins.
    private const double ConeTangent = 2.0;            // reject candidates more than ~63 deg off the travel axis
    private const double CrossGapWeight = 2.0;         // penalty for cross-axis (lateral) separation
    private const double PreferredWeight = 0.5;        // tie-break pull toward the remembered column/row
    private const double DirEpsilon = 0.5;             // px a candidate must lead by to count as "in front"

    /// <summary>Directional intent.</summary>
    public enum Dir
    {
        /// <summary>Up.</summary>
        Up = 0,

        /// <summary>Down.</summary>
        Down = 1,

        /// <summary>Left.</summary>
        Left = 2,

        /// <summary>Right.</summary>
        Right = 3,
    }

    /// <summary>Axis-aligned rectangle in window space for the PURE neighbor-pick math (decoupled from MewUI
    /// so PickNeighbor is trivially unit-testable over synthetic layouts).</summary>
    public readonly struct FocusRect
    {
        /// <summary>Left edge.</summary>
        public readonly double X;

        /// <summary>Top edge.</summary>
        public readonly double Y;

        /// <summary>Width.</summary>
        public readonly double W;

        /// <summary>Height.</summary>
        public readonly double H;

        /// <summary>Creates a rect.</summary>
        /// <param name="x">Left edge.</param>
        /// <param name="y">Top edge.</param>
        /// <param name="w">Width.</param>
        /// <param name="h">Height.</param>
        public FocusRect(double x, double y, double w, double h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }
        /// <summary>Horizontal center.</summary>
        public double CenterX => X + (W * 0.5);
        /// <summary>Vertical center.</summary>
        public double CenterY => Y + (H * 0.5);
        /// <summary>Left edge.</summary>
        public double Left => X;
        /// <summary>Right edge.</summary>
        public double Right => X + W;
        /// <summary>Top edge.</summary>
        public double Top => Y;
        /// <summary>Bottom edge.</summary>
        public double Bottom => Y + H;
    }

    // Reused buffers (no steady-state alloc).
    private readonly List<UIElement> _focusables = [];
    private readonly List<FocusRect> _rects = [];

    // Aggregated edge/repeat state (scalars over ALL gamepad-kind devices).
    private readonly bool[] _prevBtn = new bool[(int)GamepadButton.Count];
    private readonly bool[] _dirHeld = new bool[4];
    private readonly double[] _dirTimer = new double[4];

    // Cross-axis memory: the column (X) kept across vertical moves and the row (Y) kept across horizontal.
    private double _preferredX, _preferredY;
    private bool _hasPreferred;

    // Engagement.
    private bool _engaged;
    private UIElement? _engagedElement;

    // Window tracking (reset state + seed initial focus on a screen change).
    private Window? _lastWindow;

    /// <summary>Optional fire-and-forget feedback callback (an audio cue, a log): called with what the pad just did.
    /// Null means no feedback.</summary>
    public Action<UiNavFeedbackKind>? FeedbackSink { get; set; }

    /// <summary>
    /// Applies one frame of gamepad input to <paramref name="window"/>'s focus. Direction comes from the left stick AND
    /// the D-pad of any connected gamepad-kind device (edge + hold-to-repeat); A / Start activate (or engage a value
    /// control), B / Back go back (or disengage).
    /// </summary>
    /// <param name="window">The top window.</param>
    /// <param name="input">This frame's input.</param>
    /// <param name="dt">The frame delta in seconds (0 = edge-only, no auto-repeat).</param>
    /// <returns>True if a focus move, activation or value change happened.</returns>
    public bool Update(Window window, RawInputSnapshot input, double dt = 0.0)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(input);
        if (window.Handle == 0)
        {
            return false;
        }

        TrackWindow(window, input);

        // The focused element went (hidden / disabled): hand the focus on, and back when it returns.
        RepairFocus(window);

        // Directional input: stick + D-pad, aggregated over all gamepad-kind devices.
        ReadDirections(input, _excluded, out bool up, out bool down, out bool left, out bool right);
        var fire = new Fired
        {
            Up = StepRepeat((int)Dir.Up, up, dt),
            Down = StepRepeat((int)Dir.Down, down, dt),
            Left = StepRepeat((int)Dir.Left, left, dt),
            Right = StepRepeat((int)Dir.Right, right, dt),

            // Activation edges (evaluate ALL so the previous state stays current even when unused).
            A = EdgeBtn(input, GamepadButton.A),
            Start = EdgeBtn(input, GamepadButton.Start),
            B = EdgeBtn(input, GamepadButton.B) | EdgeBtn(input, GamepadButton.Back),
            X = EdgeBtn(input, GamepadButton.X),
            Y = EdgeBtn(input, GamepadButton.Y),
            Lb = EdgeBtn(input, GamepadButton.LeftBumper),
            Rb = EdgeBtn(input, GamepadButton.RightBumper),
        };

        // A screen that reads the pads itself (GamepadClaim) gets them alone: the edges above stay current, nothing
        // acts on its window.
        if (GamepadClaim.IsClaimed(window))
        {
            return false;
        }

        // Start is the screen's Confirm when it has one; otherwise it activates like A.
        bool startConfirms = fire.Start && NavCommands.CanExecute(window, NavCommands.Confirm);
        bool aEdge = fire.A || (fire.Start && !startConfirms);

        bool acted = DispatchFocusInput(window, in fire, aEdge);
        if (startConfirms)
        {
            acted |= Command(window, NavCommands.Confirm);
        }

        acted |= DispatchScreenCommands(window, in fire);
        return acted;
    }

    /// <summary>The edges and repeats one frame produced.</summary>
    private struct Fired
    {
        public bool Up, Down, Left, Right, A, Start, B, X, Y, Lb, Rb;

        public readonly bool AnyDirection => Up || Down || Left || Right;
    }

    // The one device this window keeps to itself (GamepadClaim.SetExcludedDevice), ignored while reading the pads. When
    // it changes, what is held NOW counts as already held (a controller-setup capture ends while the pad still holds
    // the button it was binding: that must not land on the window as a fresh A / B edge). A new window starts the same
    // way.
    private void TrackWindow(Window window, RawInputSnapshot input)
    {
        string? excluded = GamepadClaim.ExcludedDevice(window);
        bool excludedChanged = !string.Equals(excluded, _excluded, StringComparison.OrdinalIgnoreCase);
        _excluded = excluded;
        if (excludedChanged && ReferenceEquals(window, _lastWindow))
        {
            SeedHeldInput(input);
        }

        if (!ReferenceEquals(window, _lastWindow))
        {
            OnWindowChanged(window);
            SeedHeldInput(input);
            _lastWindow = window;
        }
    }

    // Directions, A and B: on the engaged control, on the parked highlight, on a screen with nothing focusable, or
    // as plain focus navigation.
    private bool DispatchFocusInput(Window window, in Fired fire, bool aEdge)
    {
        FocusManager fm = window.FocusManager;
        if (_engaged)
        {
            return UpdateEngaged(window, fire.Up, fire.Down, fire.Left, fire.Right, aEdge, fire.B);
        }

        if (fm.FocusedElement is null && (aEdge || fire.AnyDirection) && HoverPark.Restore(window))
        {
            // The mouse left the controls and took the highlight with it (HoverPark): this press only brings it back.
            SeedPreferred(window);
            return true;
        }

        bool acted = false;
        if (fm.FocusedElement is null && (fire.A || fire.B || fire.Left || fire.Right) && !HasFocusable(window))
        {
            // Nothing to focus: the screen's legend is the whole interface (A confirms, Left / Right cycle, B backs).
            acted |= fire.A && Command(window, NavCommands.Confirm);
            acted |= fire.B && Back(window);
            acted |= fire.Left && Command(window, NavCommands.CycleLeft);
            acted |= fire.Right && Command(window, NavCommands.CycleRight);
            return acted;
        }

        acted |= fire.Up && MoveFocus(window, Dir.Up);
        acted |= fire.Down && MoveFocus(window, Dir.Down);
        acted |= fire.Left && MoveFocus(window, Dir.Left);
        acted |= fire.Right && MoveFocus(window, Dir.Right);
        acted |= aEdge && ActivateOrEngage(window);
        acted |= fire.B && Back(window);
        return acted;
    }

    // X / Y / LB / RB run the screen-level navigation commands whatever the focus state.
    private bool DispatchScreenCommands(Window window, in Fired fire)
    {
        bool acted = false;
        acted |= fire.X && Command(window, NavCommands.ActionX);
        acted |= fire.Y && Command(window, NavCommands.ActionY);
        acted |= fire.Lb && Command(window, NavCommands.PrevTab);
        acted |= fire.Rb && Command(window, NavCommands.NextTab);
        return acted;
    }

    // ---------------------------------------------------------------- directional navigation

    /// <summary>Move focus to the best focusable in <paramref name="dir"/> from the currently focused element,
    /// wrapping at edges (unless <paramref name="wrap"/> is false). With no current focus, focuses the first
    /// focusable. Returns true if focus changed.</summary>
    public bool MoveFocus(Window window, Dir dir, bool wrap = true)
    {
        CollectFocusables(window);
        if (_focusables.Count == 0)
        {
            return false;
        }

        FocusManager fm = window.FocusManager;
        UIElement? current = fm.FocusedElement;
        if (current is null)
        {
            bool seeded = fm.SetFocus(_focusables[0]);
            if (seeded)
            {
                SeedPreferred(window);
                Feedback(UiNavFeedbackKind.Move);
            }

            return seeded;
        }

        // The focused control may name where this direction goes (IFocusDirectionTarget); else the spatial pick.
        if (current is IFocusDirectionTarget named && named.FocusTargetFor(ToFocusDirection(dir)) is { } target
            && !ReferenceEquals(target, current) && _focusables.Contains(target) && fm.SetFocus(target))
        {
            SeedPreferred(window);
            Feedback(UiNavFeedbackKind.Move);
            return true;
        }

        int curIdx = _focusables.IndexOf(current);
        FocusRect cur = curIdx >= 0 ? _rects[curIdx] : ToFocusRect(WindowBounds(current, window));

        if (!_hasPreferred)
        {
            _preferredX = cur.CenterX;
            _preferredY = cur.CenterY;
            _hasPreferred = true;
        }

        bool vertical = dir is Dir.Up or Dir.Down;
        double preferredCross = vertical ? _preferredX : _preferredY;

        Span<FocusRect> span = CollectionsMarshal.AsSpan(_rects);
        int pick = PickNeighbor(cur, span, dir, preferredCross, curIdx);
        if (pick < 0 && wrap)
        {
            pick = PickWrap(span, dir, preferredCross, curIdx);
        }

        if (pick < 0)
        {
            return false;
        }

        bool moved = fm.SetFocus(_focusables[pick]);
        if (!moved)
        {
            return false;
        }

        // Update cross-axis memory: a move ALONG an axis keeps the cross coordinate; it updates the OTHER axis
        // to the new element so the perpendicular move "remembers" where you are.
        FocusRect chosen = _rects[pick];
        if (vertical)
        {
            _preferredY = chosen.CenterY;
        }
        else
        {
            _preferredX = chosen.CenterX;
        }

        Feedback(UiNavFeedbackKind.Move);
        return true;
    }

    /// <summary>
    /// PURE overlap-aware angular neighbor pick. From <paramref name="current"/>, choose the index in
    /// <paramref name="candidates"/> (skipping <paramref name="skipIndex"/>) that best continues travel in
    /// <paramref name="dir"/>: the candidate must lead on the travel axis and lie within an angular cone; among
    /// those, the score prefers small travel distance, small cross-axis GAP (so axis-overlapping candidates —
    /// same column/row — win, WinUI/Chromium style), and proximity to <paramref name="preferredCross"/> (the
    /// remembered column/row). Returns -1 when nothing qualifies (caller may then wrap).
    /// </summary>
    public static int PickNeighbor(FocusRect current, ReadOnlySpan<FocusRect> candidates, Dir dir, double preferredCross, int skipIndex = -1)
    {
        bool vertical = dir is Dir.Up or Dir.Down;
        double sign = (dir is Dir.Down or Dir.Right) ? 1.0 : -1.0;

        int best = -1;
        double bestScore = double.MaxValue;
        for (int i = 0; i < candidates.Length; i++)
        {
            if (i == skipIndex)
            {
                continue;
            }

            FocusRect c = candidates[i];

            double curPrimary = vertical ? current.CenterY : current.CenterX;
            double candPrimary = vertical ? c.CenterY : c.CenterX;
            double primaryDelta = (candPrimary - curPrimary) * sign;
            if (primaryDelta <= DirEpsilon)
            {
                continue;   // not ahead in the requested direction
            }

            double curCross = vertical ? current.CenterX : current.CenterY;
            double candCross = vertical ? c.CenterX : c.CenterY;
            double crossCenterDelta = Math.Abs(candCross - curCross);
            if (crossCenterDelta > primaryDelta * ConeTangent)
            {
                continue;   // outside the directional cone
            }

            double crossGap = vertical
                ? Math.Max(0.0, Math.Max(current.Left - c.Right, c.Left - current.Right))
                : Math.Max(0.0, Math.Max(current.Top - c.Bottom, c.Top - current.Bottom));
            double prefDelta = Math.Abs(candCross - preferredCross);

            double score = primaryDelta + (CrossGapWeight * crossGap) + (PreferredWeight * prefDelta);
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    /// <summary>PURE wrap pick: with no candidate ahead in <paramref name="dir"/>, jump to the focusable
    /// farthest in the OPPOSITE direction (Down at the bottom goes to the topmost), tie-broken toward
    /// <paramref name="preferredCross"/>. Returns -1 only when there is no other focusable.</summary>
    public static int PickWrap(ReadOnlySpan<FocusRect> candidates, Dir dir, double preferredCross, int skipIndex = -1)
    {
        bool vertical = dir is Dir.Up or Dir.Down;
        double sign = (dir is Dir.Down or Dir.Right) ? 1.0 : -1.0;

        int best = -1;
        double bestPrimary = double.MaxValue;
        double bestPref = double.MaxValue;
        for (int i = 0; i < candidates.Length; i++)
        {
            if (i == skipIndex)
            {
                continue;
            }

            FocusRect c = candidates[i];
            double candPrimary = (vertical ? c.CenterY : c.CenterX) * sign;   // minimize: the farthest opposite
            double candCross = vertical ? c.CenterX : c.CenterY;
            double pref = Math.Abs(candCross - preferredCross);
            if (candPrimary < bestPrimary - 1e-9 || (Math.Abs(candPrimary - bestPrimary) <= 1e-9 && pref < bestPref))
            {
                bestPrimary = candPrimary;
                bestPref = pref;
                best = i;
            }
        }

        return best;
    }

    // ---------------------------------------------------------------- engagement

    private bool ActivateOrEngage(Window window)
    {
        UIElement? fe = window.FocusManager.FocusedElement;
        if (fe is null)
        {
            return false;
        }

        if (IsValueControl(fe))
        {
            _engaged = true;
            _engagedElement = fe;
            (fe as IGamepadValueControl)?.OnGamepadEngaged();
            Feedback(UiNavFeedbackKind.Engage);
            return true;
        }

        return Activate(window, MewKey.Enter, UiNavFeedbackKind.Activate);
    }

    private bool UpdateEngaged(Window window, bool fireUp, bool fireDown, bool fireLeft, bool fireRight, bool aEdge, bool bEdge)
    {
        UIElement? fe = window.FocusManager.FocusedElement;
        if (fe is null || !ReferenceEquals(fe, _engagedElement))
        {
            // Focus changed out from under us (e.g. mouse) — drop engagement silently (an uncommitted choice is abandoned).
            (_engagedElement as IGamepadValueControl)?.OnGamepadDisengaged(confirm: false);
            _engaged = false;
            _engagedElement = null;
            return false;
        }

        if (aEdge || bEdge)
        {
            Disengage(fe, confirm: aEdge);
            return true;
        }

        bool isCombo = fe is ComboBox or DropDownBase;
        bool acted = false;
        // Horizontal adjusts the value / cycles items; for a combo, Left/Right map to prev/next (Up/Down keys).
        if (fireLeft)
        {
            acted |= RouteKey(window, isCombo ? MewKey.Up : MewKey.Left);
        }

        if (fireRight)
        {
            acted |= RouteKey(window, isCombo ? MewKey.Down : MewKey.Right);
        }
        // Vertical also adjusts (sliders) / cycles (combos) for convenience.
        if (fireUp)
        {
            acted |= RouteKey(window, MewKey.Up);
        }

        if (fireDown)
        {
            acted |= RouteKey(window, MewKey.Down);
        }

        if (acted)
        {
            Feedback(UiNavFeedbackKind.Adjust);
        }

        return acted;
    }

    private void Disengage(UIElement fe, bool confirm)
    {
        if (fe is DropDownBase dd)
        {
            dd.IsDropDownOpen = false;
        }

        (fe as IGamepadValueControl)?.OnGamepadDisengaged(confirm);
        _engaged = false;
        _engagedElement = null;
        Feedback(UiNavFeedbackKind.Disengage);
    }

    // IGamepadValueControl lets an application's own value controls (plain Control subclasses) opt in.
    private static bool IsValueControl(UIElement e)
    {
        return e is RangeBase or ComboBox or DropDownBase or IGamepadValueControl;
    }

    // ---------------------------------------------------------------- key routing + activation

    private bool Activate(Window window, MewKey key, UiNavFeedbackKind kind)
    {
        if (window.FocusManager.FocusedElement is null)
        {
            return false;
        }

        bool ok = RouteKey(window, key);
        if (ok)
        {
            Feedback(kind);
        }

        return ok;
    }

    private bool Command(Window window, Command command)
    {
        bool ran = NavCommands.Execute(window, command);
        if (ran)
        {
            Feedback(UiNavFeedbackKind.Activate);
        }

        return ran;
    }

    // Walks the tree, so it runs only on an input edge, never on an idle frame.
    private bool HasFocusable(Window window)
    {
        CollectFocusables(window);
        return _focusables.Count > 0;
    }

    private static bool RouteKey(Window window, MewKey key)
    {
        if (window.FocusManager.FocusedElement is null)
        {
            return false;
        }

        RouteKeyHandled(window, key);
        return true;
    }

    // Deliver a key press to the focused element (bubbling, then the window's input map); true when someone handled it.
    private static bool RouteKeyHandled(Window window, MewKey key)
    {
        var down = new KeyEventArgs(key, 0);
        WindowInputRouter.KeyDown(window, down);
        WindowInputRouter.KeyUp(window, new KeyEventArgs(key, 0));
        return down.Handled;
    }

    // B / Back: Escape to the focused element first (an open list closes, an edit cancels) - which reaches the
    // window's input map too, where Escape is the screen's Back (NavCommands.MapKeys); with nothing focused, Back
    // directly. One path for the pad and the keyboard.
    private bool Back(Window window)
    {
        bool done = window.FocusManager.FocusedElement is not null
            ? RouteKeyHandled(window, MewKey.Escape)
            : NavCommands.Execute(window, NavCommands.Back);
        if (done)
        {
            Feedback(UiNavFeedbackKind.Activate);
        }

        return done;
    }

    // ---------------------------------------------------------------- input aggregation 

    /// <summary>OR a direction over EVERY connected gamepad-kind device's stick + D-pad. Non-gamepad HID
    /// (a joystick without a mapping) is skipped so it cannot drive the menu; any real pad can.</summary>
    private static void ReadDirections(RawInputSnapshot input, string? excluded, out bool up, out bool down, out bool left, out bool right)
    {
        up = down = left = right = false;
        if (input.GamepadConnected && input.GamepadKind == DeviceKind.Gamepad && !GamepadClaim.IsExcluded(excluded, input.GamepadGuid))
        {
            Accumulate(input.Axis(GamepadAxis.LeftX), input.Axis(GamepadAxis.LeftY), input.GamepadButtonDown, ref up, ref down, ref left, ref right);
        }

        for (int j = 0; j < input.Devices.Count; j++)
        {
            DeviceSnapshot d = input.Devices[j];
            if (!d.Connected || d.Kind != DeviceKind.Gamepad || GamepadClaim.IsExcluded(excluded, d.DeviceGuid))
            {
                continue;
            }

            Accumulate(d.Axis(GamepadAxis.LeftX), d.Axis(GamepadAxis.LeftY), d.Buttons, ref up, ref down, ref left, ref right);
        }
    }

    private static void Accumulate(float lx, float ly, bool[] buttons, ref bool up, ref bool down, ref bool left, ref bool right)
    {
        up |= ly < -StickThreshold || Btn(buttons, GamepadButton.DPadUp);
        down |= ly > StickThreshold || Btn(buttons, GamepadButton.DPadDown);
        left |= lx < -StickThreshold || Btn(buttons, GamepadButton.DPadLeft);
        right |= lx > StickThreshold || Btn(buttons, GamepadButton.DPadRight);
    }

    private static bool Btn(bool[] buttons, GamepadButton b)
    {
        return (int)b < buttons.Length && buttons[(int)b];
    }

    /// <summary>True if any connected gamepad-kind device currently holds <paramref name="b"/>.</summary>
    private static bool AnyButton(RawInputSnapshot input, GamepadButton b, string? excluded)
    {
        if (input.GamepadConnected && input.GamepadKind == DeviceKind.Gamepad && input.IsButtonDown(b)
            && !GamepadClaim.IsExcluded(excluded, input.GamepadGuid))
        {
            return true;
        }

        for (int j = 0; j < input.Devices.Count; j++)
        {
            DeviceSnapshot d = input.Devices[j];
            if (d.Connected && d.Kind == DeviceKind.Gamepad && d.IsButtonDown(b) && !GamepadClaim.IsExcluded(excluded, d.DeviceGuid))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The device the current window keeps to itself (GamepadClaim.SetExcludedDevice), read each update.</summary>
    private string? _excluded;

    // A new window starts with what is held NOW as already held: the B that closed the last screen, the Start that
    // opened this one or a direction still pushed must be released and pressed again to act here (a held B would
    // otherwise back out of the new screen too, a held Start activate its first item). The same when a window hands a
    // device back: the button or stick a controller-setup screen was capturing is still held at that moment. A held
    // direction is blocked until it is RELEASED: seeded as a normal hold, the hold-to-repeat would fire after the
    // initial delay and start scanning the focus 0.4 s later.
    private void SeedHeldInput(RawInputSnapshot input)
    {
        for (int i = 0; i < _prevBtn.Length; i++)
        {
            _prevBtn[i] = AnyButton(input, (GamepadButton)i, _excluded);
        }

        ReadDirections(input, _excluded, out bool up, out bool down, out bool left, out bool right);
        Seed((int)Dir.Up, up);
        Seed((int)Dir.Down, down);
        Seed((int)Dir.Left, left);
        Seed((int)Dir.Right, right);

        void Seed(int dir, bool held)
        {
            _dirHeld[dir] = held;
            _dirTimer[dir] = held ? double.PositiveInfinity : 0.0;   // held: no repeat until a release resets it
        }
    }

    private bool EdgeBtn(RawInputSnapshot input, GamepadButton b)
    {
        bool now = AnyButton(input, b, _excluded);
        bool edge = now && !_prevBtn[(int)b];
        _prevBtn[(int)b] = now;
        return edge;
    }

    /// <summary>Edge + hold-to-repeat for one direction: fire on press, then after the initial delay
    /// repeat at the interval while held; reset on release.</summary>
    private bool StepRepeat(int dir, bool now, double dt)
    {
        if (!now)
        {
            _dirHeld[dir] = false;
            _dirTimer[dir] = 0.0;
            return false;
        }

        if (!_dirHeld[dir])
        {
            _dirHeld[dir] = true;
            _dirTimer[dir] = RepeatInitialDelay;
            return true;
        }

        _dirTimer[dir] -= dt;
        if (_dirTimer[dir] <= 0.0)
        {
            _dirTimer[dir] += RepeatInterval;
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- focusable collection + geometry

    private void CollectFocusables(Window window)
    {
        _focusables.Clear();
        _rects.Clear();
        CollectCore(window.Content);
        // Project to window-space bounds in the SAME order (parallel arrays for the pure pick).
        for (int i = 0; i < _focusables.Count; i++)
        {
            _rects.Add(ToFocusRect(WindowBounds(_focusables[i], window)));
        }
    }

    private void CollectCore(Element? element)
    {
        if (element is UIElement ue && (!ue.IsVisible || !ue.IsEffectivelyEnabled))
        {
            return;
        }

        if (element is UIElement u && u.Focusable && u.IsEffectivelyEnabled && u.IsVisible)
        {
            _focusables.Add(u);
        }

        if (element is IVisualTreeHost host)
        {
            host.VisitChildren(child =>
            {
                CollectCore(child);
                return true;
            });
        }
    }

    private static FocusRect ToFocusRect(Rect r)
    {
        return new(r.X, r.Y, r.Width, r.Height);
    }

    private static FocusDirection ToFocusDirection(Dir d)
    {
        return d switch
        {
            Dir.Up => FocusDirection.Up,
            Dir.Down => FocusDirection.Down,
            Dir.Left => FocusDirection.Left,
            _ => FocusDirection.Right,
        };
    }

    private static Rect WindowBounds(UIElement e, Window window)
    {
        Element root = window.Content ?? window;
        var local = new Rect(0, 0, e.Bounds.Width, e.Bounds.Height);
        try
        {
            return e.TranslateRect(local, root);
        }
        catch (InvalidOperationException)
        {
            return e.Bounds;
        }
    }

    // ---------------------------------------------------------------- lifecycle

    private void OnWindowChanged(Window window)
    {
        // Reset per-window transient state so a new screen starts clean.
        Array.Clear(_prevBtn);
        Array.Clear(_dirHeld);
        Array.Clear(_dirTimer);
        (_engagedElement as IGamepadValueControl)?.OnGamepadDisengaged(confirm: false);
        _engaged = false;
        _engagedElement = null;
        _hasPreferred = false;
        _parkedFrom = _parkedTo = null;

        // Guarantee an initial focus cursor on a screen that has focusables. Skip read-only screens
        // (a HUD) which expose none.
        // A window coming back with its highlight parked by the mouse (HoverPark) stays parked: no cursor until a press.
        if (HoverPark.IsParked(window))
        {
            return;
        }

        if (ApplyInitialFocus(window) || window.FocusManager.FocusedElement is not null)
        {
            SeedPreferred(window);
        }
    }

    /// <summary>The focus a screen starts with: when nothing in <paramref name="window"/> has focus, its first
    /// focusable element in tree order gets it (a read-only HUD has none). The navigator applies
    /// this when it first sees a window; an offscreen render of a screen applies it too, so it shows the screen as
    /// the user first sees it (the default-focused item in its selected state). Returns true if focus was set.</summary>
    public static bool ApplyInitialFocus(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        FocusManager fm = window.FocusManager;
        if (fm.FocusedElement is not null)
        {
            return false;
        }

        UIElement? first = FirstFocusable(window.Content);
        return first is not null && fm.SetFocus(first);
    }

    private static UIElement? FirstFocusable(Element? element)
    {
        if (element is UIElement ue && (!ue.IsVisible || !ue.IsEffectivelyEnabled))
        {
            return null;
        }

        if (element is UIElement u && u.Focusable)
        {
            return u;
        }

        UIElement? found = null;
        if (element is IVisualTreeHost host)
        {
            host.VisitChildren(child =>
            {
                found = FirstFocusable(child);
                return found is null;
            });
        }

        return found;
    }

    // ---------------------------------------------------------------- focus repair
    // MewUI keeps the focus on an element that is hidden or disabled, so A would activate something the user cannot
    // see (see FocusFallback). Each frame: if the focused element cannot hold the focus, move it to the
    // screen's fallback (else the first focusable) and remember both; while the user leaves the focus where the repair
    // put it, give it back to the original element as soon as that can hold it again (a primary button that re-enables after
    // a capture: the user's next A is that button again, not Back).
    private UIElement? _parkedFrom, _parkedTo;

    /// <summary>One frame of focus repair on <paramref name="window"/> (see above). Returns true if it moved the focus.</summary>
    public bool RepairFocus(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        FocusManager fm = window.FocusManager;
        UIElement? fe = fm.FocusedElement;
        if (_parkedFrom is not null)
        {
            if (!ReferenceEquals(fe, _parkedTo))
            {
                _parkedFrom = _parkedTo = null;          // the user moved it: keep theirs
            }
            else if (FocusFallback.CanHoldFocus(_parkedFrom) && fm.SetFocus(_parkedFrom))
            {
                _parkedFrom = _parkedTo = null;
                SeedPreferred(window);
                return true;
            }
            else
            {
                // The element the focus came from is still gone, but the screen's fallback may have changed since the
                // focus was parked: a primary button that re-enables while the element the focus came from stays hidden
                // would otherwise leave the parked focus on Back. Follow the fallback; the parked-from element still wins
                // if it comes back.
                UIElement? fallback = FocusFallback.Get(window);
                if (fallback is not null && !ReferenceEquals(fallback, fe) && FocusFallback.CanHoldFocus(fallback) && fm.SetFocus(fallback))
                {
                    _parkedTo = fallback;
                    SeedPreferred(window);
                    return true;
                }
            }
        }

        if (fe is null || FocusFallback.CanHoldFocus(fe))
        {
            return false;
        }

        UIElement? target = FocusFallback.Get(window);
        if (!FocusFallback.CanHoldFocus(target))
        {
            target = FirstFocusable(window.Content);
        }

        if (target is not null && fm.SetFocus(target))
        {
            _parkedFrom ??= fe;   // a second repair keeps the FIRST element (it is the one to restore)

            _parkedTo = target;
            SeedPreferred(window);
            return true;
        }

        fm.ClearFocus();
        return true;
    }

    private void SeedPreferred(Window window)
    {
        UIElement? fe = window.FocusManager.FocusedElement;
        if (fe is null)
        {
            _hasPreferred = false;
            return;
        }

        FocusRect r = ToFocusRect(WindowBounds(fe, window));
        _preferredX = r.CenterX;
        _preferredY = r.CenterY;
        _hasPreferred = true;
    }

    /// <summary>Nothing to release (the navigator draws nothing); kept so hosts that own the navigator need not change.</summary>
    public void Dispose() { }

    private void Feedback(UiNavFeedbackKind kind)
    {
        FeedbackSink?.Invoke(kind);
    }
}
