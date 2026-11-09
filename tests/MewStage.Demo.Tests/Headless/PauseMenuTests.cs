using Aprillz.MewUI.Controls;

using MewStage.Demo.Overlay;
using MewStage.Demo.Tests.Support;
using MewStage.Input;
using MewStage.Ui.Backend;
using MewStage.Ui.Navigation;

using Xunit;

namespace MewStage.Demo.Tests.Headless;

/// <summary>
/// The pause menu's wiring without a GPU: Escape reaches the window's InputMap only after the focused control declined
/// it, typed text reaches the TextBox through the SDL bridge and lands in the shared view model, the gamepad navigator
/// moves focus over the panel and A / Start run the screen's handlers, and the buttons gray out with no handler.
/// </summary>
public sealed class PauseMenuTests
{
    private sealed class Rig : System.IDisposable
    {
        public Rig()
        {
            Vm = new HudViewModel();
            Menu = PauseMenu.Build(Vm, () => Resumed++, () => ToStart++, () => Quit++);
            Screen = new HeadlessScreen(1280, 720, designSpace: true);
            Screen.Show(Menu.Window);
            Screen.Frame();
        }

        public HudViewModel Vm { get; }

        public PauseMenu Menu { get; }

        public HeadlessScreen Screen { get; }

        public int Resumed { get; private set; }

        public int ToStart { get; private set; }

        public int Quit { get; private set; }

        /// <summary>Press and release a key through the bridge (edge-diffed), running a frame after each.</summary>
        public void Tap(KeyCode key)
        {
            var down = new RawInputSnapshot();
            down.KeyDown[(int)key] = true;
            Screen.Bridge.Dispatch(Menu.Window, down);
            Screen.Frame();
            Screen.Bridge.Dispatch(Menu.Window, new RawInputSnapshot());
            Screen.Frame();
        }

        public void Dispose()
        {
            Screen.Dispose();
        }
    }

    private static RawInputSnapshot Pad(GamepadButton? held = null)
    {
        var s = new RawInputSnapshot { GamepadConnected = true, GamepadKind = DeviceKind.Gamepad };
        if (held is { } b)
        {
            s.GamepadButtonDown[(int)b] = true;
        }

        return s;
    }

    [Fact]
    public void Escape_ClosesAnOpenDropDownFirst_ThenRunsBack()
    {
        using var rig = new Rig();
        rig.Menu.Window.FocusManager.SetFocus(rig.Menu.Colors);
        rig.Menu.Colors.IsDropDownOpen = true;
        rig.Screen.Frame();

        rig.Tap(KeyCode.Escape);
        Assert.False(rig.Menu.Colors.IsDropDownOpen);
        Assert.Equal(0, rig.Resumed);   // the ComboBox handled Escape: the InputMap never saw it

        rig.Tap(KeyCode.Escape);
        Assert.Equal(1, rig.Resumed);   // nothing handled it: Back ran the screen's handler
    }

    [Fact]
    public void Enter_WhileNothingIsFocused_RunsConfirm()
    {
        using var rig = new Rig();
        rig.Menu.Window.FocusManager.ClearFocus();
        rig.Tap(KeyCode.Enter);
        Assert.Equal(1, rig.Resumed);
    }

    [Fact]
    public void TypedText_ReachesTheTextBox_AndTheViewModel()
    {
        using var rig = new Rig();
        Assert.True(rig.Menu.Label.Focus());
        var typing = new RawInputSnapshot();
        typing.AppendTextInput("hey");
        rig.Screen.Bridge.Dispatch(rig.Menu.Window, typing);
        rig.Screen.Frame();
        Assert.Equal("hey", rig.Menu.Label.Text);
        Assert.Equal("hey", rig.Vm.Label.Value);

        // The HUD shows the same value through its own binding.
        var hud = HudView.Build(rig.Vm);
        using var hudScreen = new HeadlessScreen(1280, 720, designSpace: true);
        hudScreen.Show(hud.Window);
        hudScreen.Frame();
        rig.Vm.Label.Set("hey there");
        Assert.True(hudScreen.FrameOnRequest());
    }

    [Fact]
    public void SettingsCommitTwoWay_FromInput_IntoTheViewModel()
    {
        using var rig = new Rig();

        // Keyboard: Right on the focused slider steps the value and commits it through the two-way binding.
        Assert.True(rig.Menu.Spin.Focus());
        rig.Tap(KeyCode.Right);
        Assert.True(rig.Vm.SpinSpeed.Value > 0.8, $"the slider did not commit: {rig.Vm.SpinSpeed.Value}");
        Assert.Equal(rig.Menu.Spin.Value, rig.Vm.SpinSpeed.Value, 9);

        // Space toggles the focused switch.
        Assert.True(rig.Menu.Grid.Focus());
        rig.Tap(KeyCode.Space);
        Assert.False(rig.Vm.ShowGrid.Value);

        // Gamepad engage model on the slider: A engages, Right adjusts, B disengages (focus stays).
        GamepadFocusNavigator nav = rig.Screen.Navigator;
        rig.Menu.Window.FocusManager.SetFocus(rig.Menu.Spin);
        nav.Update(rig.Menu.Window, Pad());
        double before = rig.Vm.SpinSpeed.Value;
        nav.Update(rig.Menu.Window, Pad(GamepadButton.A));
        rig.Screen.Frame();
        nav.Update(rig.Menu.Window, Pad());
        nav.Update(rig.Menu.Window, Pad(GamepadButton.DPadRight));
        rig.Screen.Frame();
        nav.Update(rig.Menu.Window, Pad());
        Assert.True(rig.Vm.SpinSpeed.Value > before, $"the engaged slider did not step: {before} -> {rig.Vm.SpinSpeed.Value}");
        Assert.Same(rig.Menu.Spin, rig.Menu.Window.FocusManager.FocusedElement);   // adjusting did not move focus
        nav.Update(rig.Menu.Window, Pad(GamepadButton.B));
        rig.Screen.Frame();
        Assert.Equal(0, rig.Resumed);   // B while engaged only disengages
        nav.Update(rig.Menu.Window, Pad());

        // The tint follows the color index.
        Assert.Equal(System.Numerics.Vector3.One, rig.Vm.Tint);
        rig.Vm.ColorIndex.Set(2);
        Assert.NotEqual(System.Numerics.Vector3.One, rig.Vm.Tint);
        rig.Screen.Frame();
        Assert.Equal(2, rig.Menu.Colors.SelectedIndex);   // source -> target
    }

    [Fact]
    public void Gamepad_NavigatesThePanel_AndStartConfirms()
    {
        using var rig = new Rig();
        GamepadFocusNavigator nav = rig.Screen.Navigator;
        nav.Update(rig.Menu.Window, Pad());   // first sight of the window: initial focus, nothing counts as pressed
        rig.Screen.Frame();
        UIElement? first = rig.Menu.Window.FocusManager.FocusedElement;
        Assert.NotNull(first);

        Assert.True(nav.Update(rig.Menu.Window, Pad(GamepadButton.DPadDown)));
        rig.Screen.Frame();
        nav.Update(rig.Menu.Window, Pad());
        UIElement? second = rig.Menu.Window.FocusManager.FocusedElement;
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        // Start is the screen's Confirm while a Confirm handler exists: Resume runs whatever is focused.
        nav.Update(rig.Menu.Window, Pad(GamepadButton.Start));
        rig.Screen.Frame();
        Assert.Equal(1, rig.Resumed);
        nav.Update(rig.Menu.Window, Pad());

        // B is Back through the same path as Escape.
        nav.Update(rig.Menu.Window, Pad(GamepadButton.B));
        rig.Screen.Frame();
        Assert.Equal(2, rig.Resumed);
    }

    [Fact]
    public void Buttons_FollowTheirCommands()
    {
        using var rig = new Rig();
        Assert.True(rig.Menu.Resume.IsEffectivelyEnabled);
        Assert.True(NavCommands.CanExecute(rig.Menu.Window, PauseMenu.QuitCommand));
        Assert.True(NavCommands.Execute(rig.Menu.Window, PauseMenu.ToStartCommand));
        Assert.Equal(1, rig.ToStart);
        Assert.True(NavCommands.Execute(rig.Menu.Window, PauseMenu.QuitCommand));
        Assert.Equal(1, rig.Quit);

        // A dialog whose Back cancels; its Quit button has its own command.
        int canceled = 0;
        int confirmed = 0;
        var dialog = QuitDialog.Build(() => canceled++, () => confirmed++);
        using var screen = new HeadlessScreen(1280, 720, designSpace: true);
        screen.Show(dialog.Window);
        screen.Frame();
        Assert.Equal(0.0, dialog.Root.Opacity);
        Assert.True(NavCommands.Execute(dialog.Window, NavCommands.Back));
        Assert.True(NavCommands.Execute(dialog.Window, QuitDialog.ConfirmQuitCommand));
        Assert.Equal((1, 1), (canceled, confirmed));
        Assert.True(dialog.Cancel.IsEffectivelyEnabled);
        Assert.True(dialog.Quit.IsEffectivelyEnabled);
    }
}
