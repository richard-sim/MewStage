using System;
using System.Collections.Generic;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.Input;

using MewStage.Input;
using MewStage.Rendering.Backends.Headless;
using MewStage.Ui.Backend;
using MewStage.Ui.Tests.Support;

using Xunit;

using MewKey = Aprillz.MewUI.Key;

namespace MewStage.Ui.Tests.Headless;

/// <summary>
/// The input bridge turns the snapshot's LEVEL state into the edge events MewUI's router wants, scales pixels to DIPs,
/// delivers typed text to the focused text client unless the keystroke's KeyDown was handled, and maps every
/// non-modifier key.
/// </summary>
[Collection(IdleHostCollection.Name)]
public sealed class SdlInputBridgeTests
{
    public SdlInputBridgeTests()
    {
        IdleHostCollection.SettleAnimations();
    }

    private sealed class KeyProbe : Button
    {
        public List<(MewKey Key, bool Down)> Events { get; } = [];

        public bool HandleKeyDown { get; set; }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            Events.Add((e.Key, true));
            if (HandleKeyDown)
            {
                e.Handled = true;
                return;
            }

            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            Events.Add((e.Key, false));
            base.OnKeyUp(e);
        }
    }

    private static (EmbeddedUiHost Host, Window Window, HeadlessDevice Device) Hosted(UIElement content, int w = 320, int h = 200, double scale = 1.0)
    {
        var dev = new HeadlessDevice();
        Window window = new Window { Padding = Thickness.Zero }.Content(content);
        var host = new EmbeddedUiHost(dev, TestAssets.DummyShader, TestAssets.DummyShader);
        host.Resize(w, h, scale);
        host.SetWindows([window]);
        HeadlessUiFrame.Run(dev, host, UiInvalidationMode.Continuous, new HeadlessGraphicsList());   // lays the tree out
        return (host, window, dev);
    }

    [Fact]
    public void Bridge_EmitsEdgesOnly_AndScalesPointer()
    {
        KeyProbe probe = new KeyProbe().Width(100).Height(40);
        Canvas canvas = new Canvas().Width(320).Height(200);
        canvas.Add(probe.CanvasPosition(20, 20));
        (EmbeddedUiHost host, Window window, HeadlessDevice dev) = Hosted(canvas);
        using (host)
        using (dev)
        {
            window.FocusManager.SetFocus(probe);
            var bridge = new SdlInputBridge { Scale = 2f };   // 2 device pixels per DIP
            var input = new RawInputSnapshot { WindowWidth = 640, WindowHeight = 400 };

            // A held key is one KeyDown, however many frames it stays down; its release is one KeyUp.
            input.KeyDown[(int)KeyCode.Space] = true;
            bridge.Dispatch(window, input);
            bridge.Dispatch(window, input);
            bridge.Dispatch(window, input);
            input.KeyDown[(int)KeyCode.Space] = false;
            bridge.Dispatch(window, input);
            Assert.Equal([(MewKey.Space, true), (MewKey.Space, false)], probe.Events);

            // A pointer at device (140, 80) is DIP (70, 40): inside the button, which the window reports under the cursor.
            input.MouseX = 140;
            input.MouseY = 80;
            bridge.Dispatch(window, input);
            UIElement? hit = WindowInputRouter.HitTest(window, new Point(70, 40));
            Assert.NotNull(hit);
            Assert.True(ReferenceEquals(hit, probe) || IsInside(hit, probe), "the scaled pointer lands on the button");

            // A press and a release are two edges with click count 1; a second press inside the double-click window is a
            // double-click (count 2).
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            bridge.Timestamp = () => now;
            input.MouseButtonDown[(int)Input.MouseButton.Left] = true;
            bridge.Dispatch(window, input);
            Assert.Equal(1, bridge.LastClickCount);
            input.MouseButtonDown[(int)Input.MouseButton.Left] = false;
            bridge.Dispatch(window, input);
            input.MouseButtonDown[(int)Input.MouseButton.Left] = true;
            bridge.Dispatch(window, input);
            Assert.Equal(2, bridge.LastClickCount);
        }
    }

    private static bool IsInside(Element e, Element ancestor)
    {
        for (Element? p = e; p is not null; p = p.Parent)
        {
            if (ReferenceEquals(p, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Bridge_TextInput_ReachesFocusedTextBox_UnlessKeyDownHandled()
    {
        TextBox box = new TextBox().Width(200).Height(30);
        Canvas canvas = new Canvas().Width(320).Height(200);
        canvas.Add(box.CanvasPosition(10, 10));
        (EmbeddedUiHost host, Window window, HeadlessDevice dev) = Hosted(canvas);
        using (host)
        using (dev)
        {
            window.FocusManager.SetFocus(box);
            var bridge = new SdlInputBridge();
            var input = new RawInputSnapshot();

            // Typed text with the key-down of a letter nobody handles: the text lands in the box and is consumed.
            input.KeyDown[(int)KeyCode.H] = true;
            input.AppendTextInput("h");
            bridge.Dispatch(window, input);
            Assert.Equal("h", box.Text);
            Assert.Equal(0, input.TextInputLength);
            input.KeyDown[(int)KeyCode.H] = false;
            bridge.Dispatch(window, input);

            // A keystroke whose KeyDown is handled (Backspace edits the text itself) must not also type its text.
            input.KeyDown[(int)KeyCode.Backspace] = true;
            input.AppendTextInput("\b");
            bridge.Dispatch(window, input);
            Assert.Equal(string.Empty, box.Text);
            input.KeyDown[(int)KeyCode.Backspace] = false;
            bridge.Dispatch(window, input);

            // The suppression is per keystroke: the next unhandled key-down ends it and its text types again.
            input.KeyDown[(int)KeyCode.I] = true;
            input.AppendTextInput("i");
            bridge.Dispatch(window, input);
            Assert.Equal("i", box.Text);

            // Another layer owning the keyboard (UiWantsKeyboard) keeps text out of the window, consumed all the same.
            input.KeyDown[(int)KeyCode.I] = false;
            bridge.Dispatch(window, input);
            input.UiWantsKeyboard = true;
            input.KeyDown[(int)KeyCode.J] = true;
            input.AppendTextInput("j");
            bridge.Dispatch(window, input);
            Assert.Equal("i", box.Text);
            Assert.Equal(0, input.TextInputLength);
        }
    }

    [Fact]
    public void Bridge_Modifiers_TravelWithMouseAndKeyEvents()
    {
        KeyProbe probe = new KeyProbe().Width(100).Height(40);
        Canvas canvas = new Canvas().Width(320).Height(200);
        canvas.Add(probe.CanvasPosition(20, 20));
        (EmbeddedUiHost host, Window window, HeadlessDevice dev) = Hosted(canvas);
        using (host)
        using (dev)
        {
            window.FocusManager.SetFocus(probe);
            ModifierKeys seen = ModifierKeys.None;
            probe.KeyDown += e => seen = e.Modifiers;
            var bridge = new SdlInputBridge();
            var input = new RawInputSnapshot();
            input.KeyDown[(int)KeyCode.LeftShift] = true;
            input.KeyDown[(int)KeyCode.LeftCtrl] = true;
            input.KeyDown[(int)KeyCode.A] = true;
            bridge.Dispatch(window, input);
            Assert.Equal(ModifierKeys.Shift | ModifierKeys.Control, seen);

            // The modifier keys themselves are never key events: the one event is A's.
            Assert.Equal([(MewKey.A, true)], probe.Events);
        }
    }

    [Fact]
    public void Bridge_TabNavigation_MovesFocus_OnlyWhenEnabled()
    {
        Button first = new Button().Content(new Label().Text("1")).Width(80).Height(30);
        Button second = new Button().Content(new Label().Text("2")).Width(80).Height(30);
        StackPanel panel = new StackPanel().Children(first, second);
        (EmbeddedUiHost host, Window window, HeadlessDevice dev) = Hosted(panel);
        using (host)
        using (dev)
        {
            window.FocusManager.SetFocus(first);
            var bridge = new SdlInputBridge { TabNavigation = false };
            var input = new RawInputSnapshot();
            input.KeyDown[(int)KeyCode.Tab] = true;
            bridge.Dispatch(window, input);
            input.KeyDown[(int)KeyCode.Tab] = false;
            bridge.Dispatch(window, input);
            Assert.Same(first, window.FocusManager.FocusedElement);   // off: Tab is the application's

            bridge.TabNavigation = true;
            input.KeyDown[(int)KeyCode.Tab] = true;
            bridge.Dispatch(window, input);
            Assert.Same(second, window.FocusManager.FocusedElement);  // on: the platform behavior
        }
    }

    [Fact]
    public void MapKey_EveryNonModifierKey_HasAMewUiKey()
    {
        var seen = new HashSet<MewKey>();
        foreach (KeyCode k in Enum.GetValues<KeyCode>())
        {
            if (k is KeyCode.None or KeyCode.Count)
            {
                continue;
            }

            MewKey mapped = SdlInputBridge.MapKey(k);
            if (SdlInputBridge.IsModifier(k))
            {
                Assert.Equal(MewKey.None, mapped);
                continue;
            }

            Assert.NotEqual(MewKey.None, mapped);
            if (k != KeyCode.NumPadEnter)
            {
                Assert.True(seen.Add(mapped), $"two keys map to {mapped}");   // the keypad Enter shares Enter on purpose
            }
        }

        Assert.NotEqual(SdlInputBridge.MapKey(KeyCode.D1), SdlInputBridge.MapKey(KeyCode.NumPad1));
        Assert.Equal(new Vector(-1, 2), SdlInputBridge.WheelDelta(1, 2));   // SDL +x is right, MewUI +X is left
    }
}
