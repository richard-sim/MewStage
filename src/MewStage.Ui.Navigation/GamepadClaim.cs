// A window whose screen reads the gamepads itself. A press-to-join lobby is the typical case: every pad is a player
// there (any button joins, A readies, Back leaves), so the pads must not ALSO drive the window's focus or its back
// gesture, or a joining player's A would activate the focused Back button and close the screen. The gamepad focus
// navigator and the host's back gesture leave a claimed window to its screen; keyboard and mouse still work in it.
using System;
using System.Runtime.CompilerServices;

using Aprillz.MewUI;

namespace MewStage.Ui.Navigation;

/// <summary>Marks a window as reading the gamepads itself (see the file header).</summary>
public static class GamepadClaim
{
    private static readonly ConditionalWeakTable<Window, object> Claimed = [];
    private static readonly object Mark = new();

    // One device left to the screen while the other pads keep driving the window: a controller-setup screen sets it
    // while it captures a controller, so the buttons being measured do not also move the focus or run Back / Confirm
    // (Escape, a click or ANOTHER pad still cancels). Keyed by the device GUID the input snapshot reports.
    private static readonly ConditionalWeakTable<Window, string> Excluded = [];

    /// <summary>Claims (or releases) the gamepads for <paramref name="window"/>.</summary>
    /// <param name="window">The window.</param>
    /// <param name="claimed">True to claim, false to release.</param>
    public static void Set(Window window, bool claimed)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (claimed)
        {
            Claimed.AddOrUpdate(window, Mark);
        }
        else
        {
            Claimed.Remove(window);
        }
    }

    /// <summary>Whether <paramref name="window"/>'s screen reads the gamepads itself.</summary>
    /// <param name="window">The window.</param>
    /// <returns>True when claimed.</returns>
    public static bool IsClaimed(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Claimed.TryGetValue(window, out _);
    }

    /// <summary>Leaves the device <paramref name="deviceGuid"/> to <paramref name="window"/>'s screen (null or empty
    /// = none): the gamepad navigator and the host's back / bound-action gestures ignore that one device on that window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="deviceGuid">The device GUID, or null.</param>
    public static void SetExcludedDevice(Window window, string? deviceGuid)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (string.IsNullOrEmpty(deviceGuid))
        {
            Excluded.Remove(window);
        }
        else
        {
            Excluded.AddOrUpdate(window, deviceGuid);
        }
    }

    /// <summary>The device GUID <paramref name="window"/> keeps to itself, or null.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The GUID, or null.</returns>
    public static string? ExcludedDevice(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Excluded.TryGetValue(window, out string? guid) ? guid : null;
    }

    /// <summary>Whether <paramref name="deviceGuid"/> is the device <paramref name="excluded"/> names (GUIDs compare
    /// case-insensitively, as everywhere in the input layer).</summary>
    /// <param name="excluded">The excluded GUID, or null.</param>
    /// <param name="deviceGuid">The device GUID to test.</param>
    /// <returns>True when they name the same device.</returns>
    public static bool IsExcluded(string? excluded, string? deviceGuid)
    {
        return !string.IsNullOrEmpty(excluded) && string.Equals(excluded, deviceGuid, StringComparison.OrdinalIgnoreCase);
    }
}
