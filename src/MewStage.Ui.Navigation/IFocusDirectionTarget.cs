// A focusable control that names where one pad direction goes from it, instead of the navigator's nearest-in-that-
// direction pick (Left from a settings row goes back to its category, not to whichever control happens to be level
// with the row). The navigator asks the FOCUSED element only; a null answer (or a target that cannot take focus)
// leaves the move to the spatial pick.
using Aprillz.MewUI.Controls;

namespace MewStage.Ui.Navigation;

/// <summary>A pad direction, as <see cref="IFocusDirectionTarget"/> sees it.</summary>
public enum FocusDirection
{
    /// <summary>Up.</summary>
    Up,

    /// <summary>Down.</summary>
    Down,

    /// <summary>Left.</summary>
    Left,

    /// <summary>Right.</summary>
    Right,
}

/// <summary>Names the element focus goes to in a direction (see the file header).</summary>
public interface IFocusDirectionTarget
{
    /// <summary>The element to focus when the pad moves <paramref name="direction"/> from this one.</summary>
    /// <param name="direction">The direction.</param>
    /// <returns>The target, or null for the navigator's own spatial pick.</returns>
    UIElement? FocusTargetFor(FocusDirection direction);
}
