using System;
using System.Collections.Generic;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;

using MewStage.Input;
using MewStage.Rendering.Backends.Headless;
using MewStage.Ui.Backend;
using MewStage.Ui.Navigation;
using MewStage.Ui.Tests.Support;

using Xunit;

using Dir = MewStage.Ui.Backend.GamepadFocusNavigator.Dir;
using FocusRect = MewStage.Ui.Backend.GamepadFocusNavigator.FocusRect;
using MewKey = Aprillz.MewUI.Key;

namespace MewStage.Ui.Tests.Headless;

/// <summary>
/// Focus navigation for a controller, in two tiers: the pure <c>PickNeighbor</c> / <c>PickWrap</c> geometry over
/// synthetic rectangles, and window-level behavior over a laid-out MewUI window (D-pad and stick moves, hold to
/// repeat, initial focus and wrap, column memory, the engage model on a value control, screen commands, the claim
/// and exclusion gates, a held button across a window change, and a parked focus following the screen's fallback).
/// </summary>
[Collection(IdleHostCollection.Name)]
public sealed class GamepadFocusNavigatorTests
{
    public GamepadFocusNavigatorTests()
    {
        IdleHostCollection.SettleAnimations();
    }

    // ---------------------------------------------------------------- pure geometry

    [Fact]
    public void Pick_VerticalList_Down_PicksNextBelow_Up_PicksAbove()
    {
        FocusRect[] cands =
        [
            new(0, 0, 10, 10),    // 0 top
            new(0, 20, 10, 10),   // 1 middle
            new(0, 40, 10, 10),   // 2 bottom
        ];
        FocusRect cur = cands[1];
        Assert.Equal(2, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Down, cur.CenterX, 1));
        Assert.Equal(0, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Up, cur.CenterX, 1));
        Assert.Equal(-1, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Left, cur.CenterY, 1));   // one column: nothing beside
    }

    [Fact]
    public void Pick_HorizontalRow_Right_PicksNext()
    {
        FocusRect[] cands = [new(0, 0, 10, 10), new(20, 0, 10, 10), new(40, 0, 10, 10)];
        FocusRect cur = cands[0];
        Assert.Equal(1, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Right, cur.CenterY, 0));
        Assert.Equal(-1, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Left, cur.CenterY, 0));
    }

    [Fact]
    public void Pick_Grid_Down_PrefersOverlappingColumn_NotNearerOffsetItem()
    {
        var cur = new FocusRect(0, 0, 10, 10);
        FocusRect[] cands =
        [
            cur,
            new(0, 30, 10, 10),    // 1 directly below, farther in Y
            new(40, 18, 10, 10),   // 2 nearer in Y but off to the side
        ];
        Assert.Equal(1, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Down, cur.CenterX, 0));
    }

    [Fact]
    public void Pick_OffConeDiagonal_IsRejected()
    {
        var cur = new FocusRect(0, 0, 10, 10);
        FocusRect[] cands = [cur, new(500, 12, 10, 10)];   // dy = 12, dx = 500: outside the directional cone
        Assert.Equal(-1, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Down, cur.CenterX, 0));
    }

    [Fact]
    public void Pick_PreferredCross_SteersTieBreak_ColumnMemory()
    {
        var cur = new FocusRect(50, 50, 10, 10);
        FocusRect[] cands = [new(0, 0, 10, 10), new(100, 0, 10, 10)];   // equal distance up, left and right
        Assert.Equal(0, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Up, 5.0));
        Assert.Equal(1, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Up, 105.0));
    }

    [Fact]
    public void PickWrap_Down_WrapsToTopmost()
    {
        FocusRect[] cands = [new(0, 0, 10, 10), new(0, 20, 10, 10), new(0, 40, 10, 10)];
        FocusRect cur = cands[2];
        Assert.Equal(-1, GamepadFocusNavigator.PickNeighbor(cur, cands, Dir.Down, cur.CenterX, 2));
        Assert.Equal(0, GamepadFocusNavigator.PickWrap(cands, Dir.Down, cur.CenterX, 2));
    }

    // ---------------------------------------------------------------- window harnesses

    private sealed class Screen : IDisposable
    {
        public Screen(UIElement content, int w, int h)
        {
            Device = new HeadlessDevice();
            Window = new Window().Content(content);
            Host = new EmbeddedUiHost(Device, TestAssets.DummyShader, TestAssets.DummyShader);
            Host.Resize(w, h, 1.0);
            Host.SetWindows([Window]);
            Layout();
        }

        public HeadlessDevice Device { get; }

        public Window Window { get; }

        public EmbeddedUiHost Host { get; }

        public void Layout()
        {
            HeadlessUiFrame.Run(Device, Host, UiInvalidationMode.Continuous, new HeadlessGraphicsList());
        }

        public void Dispose()
        {
            Host.Dispose();
            Device.Dispose();
        }
    }

    private static (Screen Screen, Button A, Button B, Button C) MakeRow()
    {
        Button a = new Button().Content(new Label().Text("A")).Width(80).Height(30);
        Button b = new Button().Content(new Label().Text("B")).Width(80).Height(30);
        Button c = new Button().Content(new Label().Text("C")).Width(80).Height(30);
        var screen = new Screen(new StackPanel { Orientation = Orientation.Horizontal }.Children(a, b, c), 600, 120);
        return (screen, a, b, c);
    }

    private static (Screen Screen, Button A, Button B, Button C) MakeColumn()
    {
        Button a = new Button().Content(new Label().Text("A")).Width(120).Height(40);
        Button b = new Button().Content(new Label().Text("B")).Width(120).Height(40);
        Button c = new Button().Content(new Label().Text("C")).Width(120).Height(40);
        var screen = new Screen(new StackPanel { Orientation = Orientation.Vertical }.Children(a, b, c), 400, 400);
        return (screen, a, b, c);
    }

    private static RawInputSnapshot Pad()
    {
        return new RawInputSnapshot { GamepadConnected = true, GamepadKind = DeviceKind.Gamepad };
    }

    private static RawInputSnapshot Press(GamepadButton button)
    {
        RawInputSnapshot s = Pad();
        s.GamepadButtonDown[(int)button] = true;
        return s;
    }

    private static RawInputSnapshot Stick(GamepadAxis axis, float value)
    {
        RawInputSnapshot s = Pad();
        s.GamepadAxes[(int)axis] = value;
        return s;
    }

    // A navigator that has seen the window once with nothing held: whatever is held on a window's first frame counts
    // as already held (see HeldButton_AcrossAWindowChange_ActsOnlyAfterARelease), so a press acts from the next frame.
    private static GamepadFocusNavigator Seeded(Window window)
    {
        var nav = new GamepadFocusNavigator();
        nav.Update(window, Pad());
        return nav;
    }

    // ---------------------------------------------------------------- D-pad and direction targets

    [Fact]
    public void Dpad_Right_MovesFocus_LikeTheStick()
    {
        (Screen screen, Button a, Button b, _) = MakeRow();
        using (screen)
        {
            GamepadFocusNavigator nav = Seeded(screen.Window);
            screen.Window.FocusManager.SetFocus(a);

            Assert.True(nav.Update(screen.Window, Press(GamepadButton.DPadRight)));
            Assert.Same(b, screen.Window.FocusManager.FocusedElement);
        }
    }

    private sealed class NamedLeftButton : Button, IFocusDirectionTarget
    {
        public UIElement? LeftTarget { get; set; }

        public UIElement? FocusTargetFor(FocusDirection direction)
        {
            return direction == FocusDirection.Left ? LeftTarget : null;
        }
    }

    [Fact]
    public void DirectionTarget_OverridesTheSpatialPick_ForThatDirectionOnly()
    {
        Button a = new Button().Content(new Label().Text("A")).Width(80).Height(30);
        Button b = new Button().Content(new Label().Text("B")).Width(80).Height(30);
        var c = new NamedLeftButton();
        c.Content(new Label().Text("C")).Width(80).Height(30);
        using var screen = new Screen(new StackPanel { Orientation = Orientation.Horizontal }.Children(a, b, c), 600, 120);
        c.LeftTarget = a;
        GamepadFocusNavigator nav = Seeded(screen.Window);

        screen.Window.FocusManager.SetFocus(c);
        Assert.True(nav.Update(screen.Window, Press(GamepadButton.DPadLeft), dt: 0.0));
        Assert.Same(a, screen.Window.FocusManager.FocusedElement);       // the named target, not the nearer B

        // Without a name the spatial pick stands: Left from C lands on B.
        nav.Update(screen.Window, Pad());
        c.LeftTarget = null;
        screen.Window.FocusManager.SetFocus(c);
        Assert.True(nav.Update(screen.Window, Press(GamepadButton.DPadLeft), dt: 0.0));
        Assert.Same(b, screen.Window.FocusManager.FocusedElement);
    }

    // ---------------------------------------------------------------- hold to repeat

    [Fact]
    public void HoldToRepeat_TapMovesOnce_HoldRepeatsAfterDelay()
    {
        (Screen screen, Button a, Button b, Button c) = MakeColumn();
        using (screen)
        {
            GamepadFocusNavigator nav = Seeded(screen.Window);
            screen.Window.FocusManager.SetFocus(a);
            RawInputSnapshot down = Stick(GamepadAxis.LeftY, 1f);

            // The press fires at once (a -> b).
            Assert.True(nav.Update(screen.Window, down, dt: 0.0));
            Assert.Same(b, screen.Window.FocusManager.FocusedElement);

            // Held below the initial delay: no further move.
            nav.Update(screen.Window, down, dt: 0.1);
            nav.Update(screen.Window, down, dt: 0.1);
            nav.Update(screen.Window, down, dt: 0.1);
            Assert.Same(b, screen.Window.FocusManager.FocusedElement);

            // Crossing the initial delay: one repeat (b -> c).
            nav.Update(screen.Window, down, dt: 0.2);
            Assert.Same(c, screen.Window.FocusManager.FocusedElement);
        }
    }

    // ---------------------------------------------------------------- initial focus and wrap

    [Fact]
    public void InitialFocus_SeededOnFirstUpdate_NoInputNeeded()
    {
        (Screen screen, Button a, _, _) = MakeRow();
        using (screen)
        {
            var nav = new GamepadFocusNavigator();
            Assert.Null(screen.Window.FocusManager.FocusedElement);

            nav.Update(screen.Window, Pad(), dt: 0.016);
            Assert.Same(a, screen.Window.FocusManager.FocusedElement);
        }
    }

    [Fact]
    public void Wrap_DownFromLast_ReturnsToFirst()
    {
        (Screen screen, Button a, _, Button c) = MakeColumn();
        using (screen)
        {
            GamepadFocusNavigator nav = Seeded(screen.Window);
            screen.Window.FocusManager.SetFocus(c);

            Assert.True(nav.Update(screen.Window, Stick(GamepadAxis.LeftY, 1f)));
            Assert.Same(a, screen.Window.FocusManager.FocusedElement);
        }
    }

    // ---------------------------------------------------------------- column memory

    [Fact]
    public void ColumnMemory_DownThenUp_ReturnsToStart()
    {
        // A 2 x 2 aligned grid: row 0 [a b], row 1 [c d].
        Button a = new Button().Content(new Label().Text("A")).Width(100).Height(40);
        Button b = new Button().Content(new Label().Text("B")).Width(100).Height(40);
        Button c = new Button().Content(new Label().Text("C")).Width(100).Height(40);
        Button d = new Button().Content(new Label().Text("D")).Width(100).Height(40);
        using var screen = new Screen(
            new StackPanel { Orientation = Orientation.Vertical }.Children(
                new StackPanel { Orientation = Orientation.Horizontal }.Children(a, b),
                new StackPanel { Orientation = Orientation.Horizontal }.Children(c, d)),
            400,
            300);

        GamepadFocusNavigator nav = Seeded(screen.Window);
        screen.Window.FocusManager.SetFocus(b);

        Assert.True(nav.Update(screen.Window, Stick(GamepadAxis.LeftY, 1f)));
        Assert.Same(d, screen.Window.FocusManager.FocusedElement);

        Assert.True(nav.Update(screen.Window, Stick(GamepadAxis.LeftY, -1f)));
        Assert.Same(b, screen.Window.FocusManager.FocusedElement);
    }

    // ---------------------------------------------------------------- the engage model

    [Fact]
    public void Engage_Slider_LeftRightAdjustsValue_BDisengages()
    {
        Slider slider = new Slider().Minimum(0).Maximum(10);
        slider.Value = 5;
        Button ok = new Button().Content(new Label().Text("OK"));
        using var screen = new Screen(new StackPanel { Orientation = Orientation.Vertical }.Children(slider, ok), 400, 200);
        GamepadFocusNavigator nav = Seeded(screen.Window);
        screen.Window.FocusManager.SetFocus(slider);
        double start = slider.Value;

        // A engages (no value change yet).
        Assert.True(nav.Update(screen.Window, Press(GamepadButton.A)));
        Assert.Equal(start, slider.Value, 6);

        // Right while engaged adjusts the value; the focus stays.
        Assert.True(nav.Update(screen.Window, Stick(GamepadAxis.LeftX, 1f), dt: 0.0));
        Assert.True(slider.Value > start);
        Assert.Same(slider, screen.Window.FocusManager.FocusedElement);

        // B disengages; Right now moves the focus off the slider.
        nav.Update(screen.Window, Press(GamepadButton.B));
        nav.Update(screen.Window, Stick(GamepadAxis.LeftX, 1f));
        Assert.NotSame(slider, screen.Window.FocusManager.FocusedElement);
    }

    [Fact]
    public void PlainButton_A_Activates_NotEngaged()
    {
        (Screen screen, Button a, _, _) = MakeRow();
        using (screen)
        {
            GamepadFocusNavigator nav = Seeded(screen.Window);
            screen.Window.FocusManager.SetFocus(a);
            bool clicked = false;
            a.Click += () => clicked = true;

            Assert.True(nav.Update(screen.Window, Press(GamepadButton.A)));
            Assert.True(clicked);

            // Not engaged: a directional press still moves the focus.
            Assert.True(nav.Update(screen.Window, Stick(GamepadAxis.LeftX, 1f)));
            Assert.NotSame(a, screen.Window.FocusManager.FocusedElement);
        }
    }

    // ---------------------------------------------------------------- screen commands

    [Fact]
    public void FaceAndShoulderButtons_RunTheirNavigationCommands_InTheWindow()
    {
        (Screen screen, Button a, _, _) = MakeRow();
        using (screen)
        {
            Window window = screen.Window;
            GamepadFocusNavigator nav = Seeded(window);
            window.FocusManager.SetFocus(a);
            var ran = new List<string>();
            window.Commands.Register(NavCommands.ActionX, () => ran.Add("x"));
            window.Commands.Register(NavCommands.ActionY, () => ran.Add("y"));
            window.Commands.Register(NavCommands.PrevTab, () => ran.Add("lb"));
            window.Commands.Register(NavCommands.NextTab, () => ran.Add("rb"));

            foreach (GamepadButton b in new[] { GamepadButton.X, GamepadButton.Y, GamepadButton.LeftBumper, GamepadButton.RightBumper })
            {
                Assert.True(nav.Update(window, Press(b)));
                nav.Update(window, Pad());   // release
            }

            Assert.Equal(["x", "y", "lb", "rb"], ran);
            Assert.Same(a, window.FocusManager.FocusedElement);   // a screen command never moves the focus
        }
    }

    [Fact]
    public void Start_RunsConfirm_WhenTheScreenHasOne_ElseActivatesTheFocusedElement()
    {
        (Screen screen, Button a, _, _) = MakeRow();
        using (screen)
        {
            Window window = screen.Window;
            GamepadFocusNavigator nav = Seeded(window);
            window.FocusManager.SetFocus(a);
            int clicks = 0;
            int confirms = 0;
            a.Click += () => clicks++;

            Assert.True(nav.Update(window, Press(GamepadButton.Start)));
            nav.Update(window, Pad());
            Assert.Equal((1, 0), (clicks, confirms));

            window.Commands.Register(NavCommands.Confirm, () => confirms++);
            Assert.True(nav.Update(window, Press(GamepadButton.Start)));
            nav.Update(window, Pad());
            Assert.Equal((1, 1), (clicks, confirms));

            Assert.True(nav.Update(window, Press(GamepadButton.A)));   // A still activates the focused element
            Assert.Equal((2, 1), (clicks, confirms));
        }
    }

    [Fact]
    public void WindowWithNothingFocusable_A_Confirms_LeftRight_Cycle()
    {
        using var screen = new Screen(new TextBlock().Text("legend only"), 400, 200);
        Window window = screen.Window;
        GamepadFocusNavigator nav = Seeded(window);
        var ran = new List<string>();
        window.Commands.Register(NavCommands.Confirm, () => ran.Add("confirm"));
        window.Commands.Register(NavCommands.CycleLeft, () => ran.Add("left"));
        window.Commands.Register(NavCommands.CycleRight, () => ran.Add("right"));

        Assert.True(nav.Update(window, Press(GamepadButton.A)));
        nav.Update(window, Pad());
        Assert.True(nav.Update(window, Press(GamepadButton.DPadLeft)));
        nav.Update(window, Pad());
        Assert.True(nav.Update(window, Press(GamepadButton.DPadRight)));
        Assert.Equal(["confirm", "left", "right"], ran);
    }

    [Fact]
    public void ClaimedWindow_PadsDoNothing_TheScreenReadsThemItself()
    {
        (Screen screen, Button a, _, _) = MakeRow();
        using (screen)
        {
            Window window = screen.Window;
            GamepadFocusNavigator nav = Seeded(window);
            window.FocusManager.SetFocus(a);
            int clicks = 0;
            int xs = 0;
            a.Click += () => clicks++;
            window.Commands.Register(NavCommands.ActionX, () => xs++);
            GamepadClaim.Set(window, true);

            Assert.False(nav.Update(window, Press(GamepadButton.A)));
            nav.Update(window, Pad());
            Assert.False(nav.Update(window, Press(GamepadButton.X)));
            nav.Update(window, Pad());
            Assert.False(nav.Update(window, Press(GamepadButton.DPadRight)));
            Assert.Equal((0, 0), (clicks, xs));
            Assert.Same(a, window.FocusManager.FocusedElement);

            GamepadClaim.Set(window, false);
            nav.Update(window, Pad());
            Assert.True(nav.Update(window, Press(GamepadButton.A)));
            Assert.Equal(1, clicks);
        }
    }

    [Fact]
    public void ButtonBoundToACommand_IsDisabled_UntilTheScreenHandlesIt_AndThePadRunsTheSameHandler()
    {
        var b = new Button { Command = NavCommands.ActionY };
        b.Content(new Label().Text("REFRESH"));
        using var screen = new Screen(new StackPanel().Children(b), 400, 200);
        Window window = screen.Window;
        Assert.False(b.IsEffectivelyEnabled);

        int ran = 0;
        window.Commands.Register(NavCommands.ActionY, () => ran++);
        screen.Host.PumpDispatcher();
        screen.Layout();
        Assert.True(b.IsEffectivelyEnabled);

        GamepadFocusNavigator nav = Seeded(window);
        Assert.True(nav.Update(window, Press(GamepadButton.Y)));
        Assert.Equal(1, ran);
    }

    [Fact]
    public void Keyboard_PageUpDown_RunTheTabCommands_EnterConfirms_OnlyWithNothingFocused()
    {
        (Screen screen, Button a, _, _) = MakeRow();
        using (screen)
        {
            Window window = screen.Window;
            NavCommands.MapKeys(window);
            var ran = new List<string>();
            window.Commands.Register(NavCommands.PrevTab, () => ran.Add("prev"));
            window.Commands.Register(NavCommands.NextTab, () => ran.Add("next"));
            window.Commands.Register(NavCommands.Confirm, () => ran.Add("confirm"));
            int clicks = 0;
            a.Click += () => clicks++;

            WindowInputRouter.KeyDown(window, new KeyEventArgs(MewKey.PageUp, 0));
            WindowInputRouter.KeyDown(window, new KeyEventArgs(MewKey.PageDown, 0));
            WindowInputRouter.KeyDown(window, new KeyEventArgs(MewKey.Enter, 0));   // nothing focused: Confirm

            // With something focused, Enter belongs to it (the button clicks), never to the screen's Confirm.
            window.FocusManager.SetFocus(a);
            WindowInputRouter.KeyDown(window, new KeyEventArgs(MewKey.Enter, 0));
            WindowInputRouter.KeyUp(window, new KeyEventArgs(MewKey.Enter, 0));
            Assert.Equal(["prev", "next", "confirm"], ran);
            Assert.Equal(1, clicks);
        }
    }

    // ---------------------------------------------------------------- device selection

    [Fact]
    public void NonGamepadPrimary_DoesNotDriveTheMenu()
    {
        (Screen screen, Button a, _, _) = MakeRow();
        using (screen)
        {
            GamepadFocusNavigator nav = Seeded(screen.Window);
            screen.Window.FocusManager.SetFocus(a);

            // The primary device is a raw joystick holding D-pad right: ignored for navigation.
            var input = new RawInputSnapshot { GamepadConnected = true, GamepadKind = DeviceKind.Joystick };
            input.GamepadButtonDown[(int)GamepadButton.DPadRight] = true;
            nav.Update(screen.Window, input);
            Assert.Same(a, screen.Window.FocusManager.FocusedElement);
        }
    }

    [Fact]
    public void AnyGamepadInDevices_CanNavigate_PastAJoystickPrimary()
    {
        (Screen screen, Button a, Button b, _) = MakeRow();
        using (screen)
        {
            GamepadFocusNavigator nav = Seeded(screen.Window);
            screen.Window.FocusManager.SetFocus(a);

            var input = new RawInputSnapshot { GamepadConnected = true, GamepadKind = DeviceKind.Joystick };
            var pad = new DeviceSnapshot { Connected = true, Kind = DeviceKind.Gamepad };
            pad.Buttons[(int)GamepadButton.DPadRight] = true;
            input.Devices.Add(pad);

            Assert.True(nav.Update(screen.Window, input));
            Assert.Same(b, screen.Window.FocusManager.FocusedElement);
        }
    }

    // ---------------------------------------------------------------- the other devices, and held input across windows

    [Fact]
    public void Keyboard_And_Mouse_StillMoveFocus_AlongsideTheGamepad()
    {
        (Screen screen, _, Button b, _) = MakeRow();
        using (screen)
        {
            Window window = screen.Window;
            GamepadFocusNavigator nav = Seeded(window);

            Assert.True(window.FocusManager.MoveFocusNext());   // Tab
            Assert.NotNull(window.FocusManager.FocusedElement);

            var bridge = new SdlInputBridge();
            var input = new RawInputSnapshot { WindowWidth = 600, WindowHeight = 120 };
            Rect bounds = b.TranslateRect(new Rect(0, 0, b.Bounds.Width, b.Bounds.Height), window.Content!);
            input.MouseX = (float)(bounds.X + (bounds.Width * 0.5));
            input.MouseY = (float)(bounds.Y + (bounds.Height * 0.5));
            bridge.Dispatch(window, input);
            input.MouseButtonDown[(int)Input.MouseButton.Left] = true;
            bridge.Dispatch(window, input);
            input.MouseButtonDown[(int)Input.MouseButton.Left] = false;
            bridge.Dispatch(window, input);

            Assert.True(nav.Update(window, Stick(GamepadAxis.LeftX, 1f)));   // the pad still works after the others
        }
    }

    [Fact]
    public void HeldButton_AcrossAWindowChange_ActsOnlyAfterARelease()
    {
        // The B that closed the last screen is still down on the new window's first frame: it must not back out of
        // the new window too.
        (Screen one, Button a1, _, _) = MakeRow();
        (Screen two, Button a2, _, _) = MakeRow();
        using (one)
        using (two)
        {
            int backs = 0;
            two.Window.Commands.Register(NavCommands.Back, () => backs++);
            NavCommands.MapKeys(two.Window);                 // Escape (B's route through the focused element) is Back
            GamepadFocusNavigator nav = Seeded(one.Window);
            one.Window.FocusManager.SetFocus(a1);
            two.Window.FocusManager.SetFocus(a2);

            RawInputSnapshot held = Press(GamepadButton.B);
            nav.Update(one.Window, held);                     // B pressed on the first window
            Assert.False(nav.Update(two.Window, held));       // still held on the second: no edge
            Assert.Equal(0, backs);
            nav.Update(two.Window, Pad());                    // released
            Assert.True(nav.Update(two.Window, Press(GamepadButton.B)));
            Assert.Equal(1, backs);
        }
    }

    [Fact]
    public void HeldDirection_WhenTheDeviceIsHandedBack_ActsOnlyAfterARelease()
    {
        // A screen that captured the pad hands it back with the stick still pushed: the held direction is blocked
        // until it is released, so the hold-to-repeat cannot fire after the initial delay.
        const string Guid = "pad-guid-handback";
        (Screen screen, Button a, Button b, _) = MakeColumn();
        using (screen)
        {
            Window window = screen.Window;
            GamepadFocusNavigator nav = Seeded(window);
            window.FocusManager.SetFocus(a);

            GamepadClaim.SetExcludedDevice(window, Guid);     // the screen keeps the pad
            for (int i = 0; i < 10; i++)
            {
                Assert.False(nav.Update(window, Down(), dt: 0.1));
            }

            Assert.Same(a, window.FocusManager.FocusedElement);

            GamepadClaim.SetExcludedDevice(window, null);     // handed back, the stick still pushed
            for (int i = 0; i < 20; i++)
            {
                Assert.False(nav.Update(window, Down(), dt: 0.1));   // 2 s: well past the repeat delay
            }

            Assert.Same(a, window.FocusManager.FocusedElement);

            nav.Update(window, Idle(), dt: 0.1);              // released...
            Assert.True(nav.Update(window, Down(), dt: 0.0)); // ...and pushed again: acts
            Assert.Same(b, window.FocusManager.FocusedElement);
        }

        static RawInputSnapshot Down()
        {
            RawInputSnapshot p = Stick(GamepadAxis.LeftY, 1f);
            p.GamepadGuid = Guid;
            return p;
        }

        static RawInputSnapshot Idle()
        {
            RawInputSnapshot p = Pad();
            p.GamepadGuid = Guid;
            return p;
        }
    }

    [Fact]
    public void ParkedFocus_FollowsTheScreensFallback_WhileTheParkedFromElementStaysGone()
    {
        // The focus sits on SKIP; SKIP hides and the CTA disables, so the focus parks on BACK (the fallback of the
        // moment). When the CTA re-enables while SKIP stays hidden, the fallback is the CTA and the focus follows it.
        (Screen screen, Button cta, Button skip, Button back) = MakeColumn();
        using (screen)
        {
            Window window = screen.Window;
            FocusFallback.Set(window, () => cta.IsEnabled ? cta : back);
            GamepadFocusNavigator nav = Seeded(window);
            window.FocusManager.SetFocus(skip);

            skip.IsVisible = false;
            cta.IsEnabled = false;
            nav.Update(window, Pad());
            Assert.Same(back, window.FocusManager.FocusedElement);

            cta.IsEnabled = true;
            nav.Update(window, Pad());
            Assert.Same(cta, window.FocusManager.FocusedElement);

            skip.IsVisible = true;                            // the element the focus came from returns, and wins
            nav.Update(window, Pad());
            Assert.Same(skip, window.FocusManager.FocusedElement);
        }
    }

    [Fact]
    public void ModalScreen_GatesInput_HostReportsWantsInput()
    {
        (Screen screen, _, _, _) = MakeRow();
        using (screen)
        {
            Assert.False(screen.Host.WantsInput);

            Assert.NotNull(screen.Window.Backend);
            screen.Window.Backend.SetEnabled(false);
            Assert.True(screen.Host.WantsInput);

            screen.Window.Backend.SetEnabled(true);
            Assert.False(screen.Host.WantsInput);
        }
    }
}
