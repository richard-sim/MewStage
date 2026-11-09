using System;
using System.Collections.Generic;
using System.Text;

using MewStage.Input;

using SDL;

using Xunit;

namespace MewStage.Host.Tests.Input;

/// <summary>
/// The SDL scancode map: every <see cref="KeyCode"/> is reachable from exactly one scancode, the keypad is distinct
/// from the main block, letters / digits / punctuation are covered (MewUI's DevTools shortcuts and text editing need
/// them), and typed text decodes from SDL's UTF-8 without allocating past the snapshot's capacity.
/// </summary>
public sealed unsafe class SdlKeysTests
{
    private static IEnumerable<SDL_Scancode> AllScancodes()
    {
        for (int i = 0; i < (int)SDL_Scancode.SDL_SCANCODE_COUNT; i++)
        {
            yield return (SDL_Scancode)i;
        }
    }

    [Fact]
    public void MapScancode_ReachesEveryKeyCode_ExactlyOnce()
    {
        var hits = new Dictionary<KeyCode, SDL_Scancode>();
        foreach (SDL_Scancode s in AllScancodes())
        {
            KeyCode k = SdlKeys.MapScancode(s);
            if (k == KeyCode.None)
            {
                continue;
            }

            Assert.False(hits.ContainsKey(k), $"{k} is produced by both {hits.GetValueOrDefault(k)} and {s}");
            hits[k] = s;
        }

        foreach (KeyCode k in Enum.GetValues<KeyCode>())
        {
            if (k is KeyCode.None or KeyCode.Count)
            {
                continue;
            }

            Assert.True(hits.ContainsKey(k), $"no scancode maps to {k}");
        }
    }

    [Fact]
    public void MapScancode_KeypadIsDistinctFromTheMainBlock()
    {
        Assert.NotEqual(SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_1), SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_KP_1));
        Assert.NotEqual(SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_RETURN), SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_KP_ENTER));
        Assert.NotEqual(SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_PERIOD), SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_KP_PERIOD));
        Assert.NotEqual(SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_MINUS), SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_KP_MINUS));
        Assert.Equal(KeyCode.NumPadEnter, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_KP_ENTER));
        Assert.Equal(KeyCode.Enter, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_RETURN));
    }

    [Fact]
    public void MapScancode_CoversLettersDigitsPunctuationAndModifiers()
    {
        Assert.Equal(KeyCode.A, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_A));
        Assert.Equal(KeyCode.Z, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_Z));
        Assert.Equal(KeyCode.D0, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_0));
        Assert.Equal(KeyCode.D9, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_9));
        Assert.Equal(KeyCode.Semicolon, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_SEMICOLON));
        Assert.Equal(KeyCode.Apostrophe, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_APOSTROPHE));
        Assert.Equal(KeyCode.Backtick, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_GRAVE));
        Assert.Equal(KeyCode.LeftBracket, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_LEFTBRACKET));
        Assert.Equal(KeyCode.Backslash, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_BACKSLASH));
        Assert.Equal(KeyCode.LeftShift, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_LSHIFT));
        Assert.Equal(KeyCode.RightCtrl, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_RCTRL));
        Assert.Equal(KeyCode.LeftAlt, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_LALT));
        Assert.Equal(KeyCode.LeftGui, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_LGUI));
        Assert.Equal(KeyCode.Menu, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_APPLICATION));
        Assert.Equal(KeyCode.F12, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_F12));
        Assert.Equal(KeyCode.None, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_UNKNOWN));
        Assert.Equal(KeyCode.None, SdlKeys.MapScancode(SDL_Scancode.SDL_SCANCODE_CAPSLOCK));   // not carried: MewUI has no role for it
    }

    [Fact]
    public void AppendText_DecodesUtf8_UpToTheNul()
    {
        var input = new RawInputSnapshot();
        byte[] utf8 = Encoding.UTF8.GetBytes("héllo 世界\0garbage");
        fixed (byte* p = utf8)
        {
            SdlKeys.AppendText(p, input);
        }

        Assert.Equal("héllo 世界", new string(input.TextInput, 0, input.TextInputLength));

        // A second event appends.
        byte[] more = Encoding.UTF8.GetBytes("!\0");
        fixed (byte* p = more)
        {
            SdlKeys.AppendText(p, input);
        }

        Assert.Equal("héllo 世界!", new string(input.TextInput, 0, input.TextInputLength));
        SdlKeys.AppendText(null, input);   // null: ignored
        Assert.Equal(9, input.TextInputLength);
    }

    [Fact]
    public void AppendText_StopsAtTheSnapshotCapacity()
    {
        var input = new RawInputSnapshot();
        byte[] big = Encoding.UTF8.GetBytes(new string('x', 200) + "\0");
        fixed (byte* p = big)
        {
            SdlKeys.AppendText(p, input);
            SdlKeys.AppendText(p, input);   // 400 chars offered; the snapshot keeps its capacity
        }

        Assert.Equal(RawInputSnapshot.TextInputCapacity, input.TextInputLength);
    }
}
