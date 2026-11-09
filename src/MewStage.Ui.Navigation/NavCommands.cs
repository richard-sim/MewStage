// The navigation commands of a controller-friendly UI. A screen says WHAT each gesture does by registering handlers
// for these commands on its Window (window.Commands: with nothing focused the command router resolves from the
// window and never walks into the content, so a handler on a content element is found only while something inside
// it has focus); the gestures themselves are bound in one place each:
//
//   command      gamepad (GamepadFocusNavigator)             keyboard (NavCommands.MapKeys)   mouse
//   Back         B / Back (Escape to the focused element,     Escape (the focused element      a Back button
//                then this)                                  first, then this)
//   Confirm      Start; A when nothing is focused            Enter when nothing is focused    the primary button
//                (with the mouse's highlight parked, HoverPark: A / Enter / Left / Right / Tab only bring it back)
//   PrevTab      LB                                          Page Up                          -
//   NextTab      RB                                          Page Down                        -
//   ActionX      X                                           -                                an X-badged button
//   ActionY      Y                                           -                                a Y-badged button
//   CycleLeft    Left when nothing is focused                Left when nothing is focused     -
//   CycleRight   Right when nothing is focused               Right when nothing is focused    -
//
// A button that shows a controller prompt binds the same command (Button.Command), so the badge, the pad button and
// the mouse click can never disagree, and the button grays out when the screen has no handler for it. X and Y have no
// keyboard keys on purpose: a letter key would also type into a focused text field.
using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;

using Aprillz.MewUI;

namespace MewStage.Ui.Navigation;

/// <summary>The navigation commands (see the file header for the gesture of each).</summary>
public static class NavCommands
{
    /// <summary>Leave the screen: its parent screen, or close an overlay.</summary>
    public static readonly Command Back = new("nav.back");

    /// <summary>The screen's primary action (Resume, Start, OK, ...).</summary>
    public static readonly Command Confirm = new("nav.confirm");

    /// <summary>The previous tab / panel / category.</summary>
    public static readonly Command PrevTab = new("nav.prev-tab");

    /// <summary>The next tab / panel / category.</summary>
    public static readonly Command NextTab = new("nav.next-tab");

    /// <summary>The screen's X-button action.</summary>
    public static readonly Command ActionX = new("nav.action-x");

    /// <summary>The screen's Y-button action.</summary>
    public static readonly Command ActionY = new("nav.action-y");

    /// <summary>Left on a screen with nothing focusable (cycle a value the screen shows, not a control).</summary>
    public static readonly Command CycleLeft = new("nav.cycle-left");

    /// <summary>Right on a screen with nothing focusable.</summary>
    public static readonly Command CycleRight = new("nav.cycle-right");

    /// <summary>Runs <paramref name="command"/> in <paramref name="window"/>'s focused context (the focused element's
    /// scope chain, then the window's).</summary>
    /// <param name="window">The window.</param>
    /// <param name="command">The command.</param>
    /// <returns>False when no handler takes it or the handler cannot execute now.</returns>
    [SuppressMessage("Reliability", "CA2012:Use ValueTasks correctly", Justification = "Consumed exactly once: completed synchronously it is read, otherwise it is left to finish on the dispatcher.")]
    public static bool Execute(Window window, Command command)
    {
        ArgumentNullException.ThrowIfNull(window);
        ValueTask<bool> pending = window.CommandRouter.ExecuteAsync(command);

        // The navigation handlers are synchronous; an asynchronous one completes on the dispatcher.
        return !pending.IsCompleted || pending.GetAwaiter().GetResult();
    }

    /// <summary>Whether the window's handlers for <paramref name="command"/> would run now.</summary>
    /// <param name="window">The window.</param>
    /// <param name="command">The command.</param>
    /// <returns>True when a handler can execute.</returns>
    public static bool CanExecute(Window window, Command command)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.CommandRouter.CanExecute(command);
    }

    /// <summary>Binds the keyboard gestures of the table in the file header into <paramref name="window"/>'s input
    /// map. A focused control sees a key first; the map only acts on a key nothing handled. Enter and Left / Right
    /// act only while nothing is focused: with a slider focused, Enter must not fire the screen's primary action.</summary>
    /// <param name="window">The window.</param>
    public static void MapKeys(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        InputMap map = window.InputMap;
        map.Map(Back, new KeyGesture(Key.Escape));
        map.Map(PrevTab, new KeyGesture(Key.PageUp));
        map.Map(NextTab, new KeyGesture(Key.PageDown));
        MapUnfocused(window, Key.Enter, Confirm);
        MapUnfocused(window, Key.Left, CycleLeft);
        MapUnfocused(window, Key.Right, CycleRight);

        // Tab with the highlight parked by the mouse (HoverPark) brings it back; otherwise Tab is the platform's.
        window.InputMap.Map(new KeySequence(new KeyGesture(Key.Tab)), () => HoverPark.Restore(window), () => HoverPark.IsParked(window));
    }

    // With the highlight parked by the mouse (HoverPark), the key only brings it back, like the pad does.
    private static void MapUnfocused(Window window, Key key, Command command)
    {
        window.InputMap.Map(
            new KeySequence(new KeyGesture(key)),
            () =>
            {
                if (!HoverPark.Restore(window))
                {
                    Execute(window, command);
                }
            },
            () => window.FocusManager.FocusedElement is null && (HoverPark.IsParked(window) || CanExecute(window, command)));
    }
}
