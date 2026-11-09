// Hover = focus, and LEAVING clears it: with FocusFollowsMouse the highlight must not stay on a control the mouse left
// unless it moved onto another control. The mouse moving onto empty space (nothing focusable under it) takes the focus
// away, so no highlight shows anywhere, and PARKS the element it was on here. The next pad direction or A, or Tab /
// Enter / Left / Right on the keyboard, only brings that highlight back (it does not also move or activate); B, Start,
// X, Y, LB and RB act as usual. A screen that rebuilds its tree while the focus is parked re-parks the equivalent
// element (Park again), so the highlight comes back on the same button.
using System;
using System.Runtime.CompilerServices;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewStage.Ui.Navigation;

/// <summary>The element a window's mouse took the focus off (see the file header).</summary>
public static class HoverPark
{
    private static readonly ConditionalWeakTable<Window, Slot> Parked = [];

    /// <summary>Parks <paramref name="element"/> as the one to bring back on <paramref name="window"/> (null = none).</summary>
    /// <param name="window">The window.</param>
    /// <param name="element">The element, or null.</param>
    public static void Park(Window window, UIElement? element)
    {
        ArgumentNullException.ThrowIfNull(window);
        Parked.GetOrCreateValue(window).Element = element;
    }

    /// <summary>Whether the mouse has the focus parked on <paramref name="window"/> (nothing is focused).</summary>
    /// <param name="window">The window.</param>
    /// <returns>True while parked.</returns>
    public static bool IsParked(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Parked.TryGetValue(window, out Slot? s) && s.Element is not null && window.FocusManager.FocusedElement is null;
    }

    /// <summary>The parked element, or null.</summary>
    /// <param name="window">The window.</param>
    /// <returns>The element, or null.</returns>
    public static UIElement? Peek(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return Parked.TryGetValue(window, out Slot? s) ? s.Element : null;
    }

    /// <summary>Forgets the parked element (something else took the focus).</summary>
    /// <param name="window">The window.</param>
    public static void Clear(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Parked.TryGetValue(window, out Slot? s))
        {
            s.Element = null;
        }
    }

    /// <summary>Brings the highlight back: focuses the parked element if it is still in the window and can hold the
    /// focus, else the window's first focusable.</summary>
    /// <param name="window">The window.</param>
    /// <returns>False when nothing was parked (the input acts as usual).</returns>
    public static bool Restore(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!Parked.TryGetValue(window, out Slot? s) || s.Element is not { } e)
        {
            return false;
        }

        s.Element = null;
        if (window.FocusManager.FocusedElement is not null)
        {
            return false;   // something already took it
        }

        if (ReferenceEquals(e.FindVisualRoot(), window) && FocusFallback.CanHoldFocus(e) && window.FocusManager.SetFocus(e))
        {
            return true;
        }

        UIElement? first = FirstFocusable(window.Content);
        if (first is not null)
        {
            window.FocusManager.SetFocus(first);
        }

        return true;
    }

    private static UIElement? FirstFocusable(Element? element)
    {
        if (element is UIElement ue && (!ue.IsVisible || !ue.IsEffectivelyEnabled))
        {
            return null;
        }

        if (element is UIElement { Focusable: true } u)
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

    private sealed class Slot
    {
        public UIElement? Element { get; set; }
    }
}
