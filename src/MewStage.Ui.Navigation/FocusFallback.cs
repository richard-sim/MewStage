// Where the focus goes when the focused element goes away. MewUI keeps the focus on an element that is hidden or
// disabled (only leaving the tree releases it), so a screen whose buttons come and go would leave the pad's A on
// something the user cannot see. The gamepad navigator repairs that each frame: it moves the focus to the element the
// screen names here (its primary button, or Back), else to the window's first focusable, and gives it back when the
// element returns, as long as the user has not moved the focus since.
using System;
using System.Runtime.CompilerServices;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewStage.Ui.Navigation;

/// <summary>The element a window's focus falls back to (see the file header).</summary>
public static class FocusFallback
{
    private static readonly ConditionalWeakTable<Window, Func<UIElement?>> Providers = [];

    /// <summary>Names the fallback of <paramref name="window"/> (asked only when a repair is needed; null = none).</summary>
    /// <param name="window">The window.</param>
    /// <param name="provider">Returns the element to fall back to, or null.</param>
    public static void Set(Window window, Func<UIElement?>? provider)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (provider is null)
        {
            Providers.Remove(window);
        }
        else
        {
            Providers.AddOrUpdate(window, provider);
        }
    }

    /// <summary>The element <paramref name="window"/>'s screen wants the focus on when the focused one goes, or null.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The element, or null.</returns>
    public static UIElement? Get(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Providers.TryGetValue(window, out Func<UIElement?>? p) ? p() : null;
    }

    /// <summary>Whether <paramref name="element"/> can hold the focus: focusable, enabled, and it and every ancestor
    /// visible. (Leaving the tree already releases the focus in MewUI; an element in a popup has the popup's root, not
    /// the window, above it, so the walk stops at whatever root it reaches.)</summary>
    /// <param name="element">The element, or null.</param>
    /// <returns>True when it can hold the focus.</returns>
    public static bool CanHoldFocus(UIElement? element)
    {
        if (element is null || !element.Focusable || !element.IsEffectivelyEnabled)
        {
            return false;
        }

        for (Element? e = element; e is not null; e = e.Parent)
        {
            if (e is UIElement { IsVisible: false })
            {
                return false;
            }
        }

        return true;
    }
}
