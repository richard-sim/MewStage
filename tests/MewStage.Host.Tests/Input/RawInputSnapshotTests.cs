using System;

using MewStage.Input;

using Xunit;

namespace MewStage.Host.Tests.Input;

/// <summary>The device-agnostic input seam: level state, per-frame deltas, typed-text capacity.</summary>
public sealed class RawInputSnapshotTests
{
    [Fact]
    public void Arrays_AreSizedByTheEnums()
    {
        var s = new RawInputSnapshot();
        Assert.Equal((int)KeyCode.Count, s.KeyDown.Length);
        Assert.Equal((int)MouseButton.Count, s.MouseButtonDown.Length);
        Assert.Equal((int)GamepadAxis.Count, s.GamepadAxes.Length);
        Assert.Equal((int)GamepadButton.Count, s.GamepadButtonDown.Length);
        Assert.Equal(RawInputSnapshot.TextInputCapacity, s.TextInput.Length);
    }

    [Fact]
    public void IsKeyDown_IsFalseForNone_AndOutOfRange()
    {
        var s = new RawInputSnapshot();
        s.KeyDown[(int)KeyCode.Space] = true;
        Assert.True(s.IsKeyDown(KeyCode.Space));
        Assert.False(s.IsKeyDown(KeyCode.None));
        Assert.False(s.IsKeyDown(KeyCode.Count));
        Assert.False(s.IsKeyDown((KeyCode)9999));
    }

    [Fact]
    public void TextInput_AppendsUpToCapacity_ThenDrops()
    {
        var s = new RawInputSnapshot();
        s.AppendTextInput("abc");
        Assert.Equal(3, s.TextInputLength);
        Assert.Equal("abc", new string(s.TextInput, 0, s.TextInputLength));

        s.AppendTextInput(new string('x', RawInputSnapshot.TextInputCapacity));
        Assert.Equal(RawInputSnapshot.TextInputCapacity, s.TextInputLength);
        Assert.Equal('a', s.TextInput[0]);
        Assert.Equal('x', s.TextInput[RawInputSnapshot.TextInputCapacity - 1]);

        s.AppendTextInput("more");   // nothing fits any more
        Assert.Equal(RawInputSnapshot.TextInputCapacity, s.TextInputLength);
    }

    [Fact]
    public void ClearDeltas_ResetsDeltas_NotLevelState()
    {
        var s = new RawInputSnapshot
        {
            MouseX = 10,
            MouseY = 20,
            MouseDeltaX = 3,
            MouseDeltaY = -4,
            MouseWheel = 1,
            MouseWheelX = -1,
            PenEvents = 2,
        };
        s.KeyDown[(int)KeyCode.A] = true;
        s.MouseButtonDown[(int)MouseButton.Left] = true;
        s.AppendTextInput("hi");

        s.ClearDeltas();

        Assert.Equal(0f, s.MouseDeltaX);
        Assert.Equal(0f, s.MouseDeltaY);
        Assert.Equal(0f, s.MouseWheel);
        Assert.Equal(0f, s.MouseWheelX);
        Assert.Equal(0, s.PenEvents);
        Assert.Equal(0, s.TextInputLength);
        Assert.Equal(10f, s.MouseX);                       // position is level state
        Assert.True(s.IsKeyDown(KeyCode.A));               // keys are level state
        Assert.True(s.IsMouseDown(MouseButton.Left));
    }

    [Fact]
    public void DeviceSnapshot_Clear_DisconnectsAndZeroes()
    {
        var d = new DeviceSnapshot { Connected = true, Name = "pad", Kind = DeviceKind.Gamepad };
        d.Axes[(int)GamepadAxis.LeftX] = 0.5f;
        d.Buttons[(int)GamepadButton.A] = true;
        Assert.Equal(0.5f, d.Axis(GamepadAxis.LeftX));
        Assert.True(d.IsButtonDown(GamepadButton.A));

        d.Clear();

        Assert.False(d.Connected);
        Assert.Equal(0f, d.Axis(GamepadAxis.LeftX));
        Assert.False(d.IsButtonDown(GamepadButton.A));
        Assert.Equal("pad", d.Name);                       // identity survives a clear
    }

    [Fact]
    public void KeyCodes_AreDistinct_AndKeypadDiffersFromMainBlock()
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        foreach (KeyCode k in Enum.GetValues<KeyCode>())
        {
            Assert.True(seen.Add((int)k), "duplicate KeyCode value " + k);
        }

        Assert.NotEqual(KeyCode.D1, KeyCode.NumPad1);
        Assert.NotEqual(KeyCode.Enter, KeyCode.NumPadEnter);
        Assert.NotEqual(KeyCode.Period, KeyCode.NumPadPeriod);
    }
}
