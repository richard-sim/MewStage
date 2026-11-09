namespace MewStage.Ui.Navigation;

/// <summary>
/// Marker for a focusable custom control that EDITS A VALUE with Left / Right (a slider, a segmented selector, a
/// cycling dropdown), so the gamepad focus navigator treats it with the ENGAGE model exactly like the stock
/// <c>RangeBase</c> / <c>ComboBox</c> / <c>DropDownBase</c>: A engages, Left / Right (with repeat) are routed to the
/// control as <c>Key.Left</c> / <c>Key.Right</c>, A or B disengages.
/// </summary>
/// <remarks>
/// Without this the navigator knows only the stock value types; on a custom control deriving from plain
/// <c>Control</c>, A would do nothing (Enter is not handled) and Left / Right would move the focus away. The control
/// keeps handling the keys itself (<c>OnKeyDown</c>), so keyboard and pad share one input path.
/// </remarks>
public interface IGamepadValueControl
{
    /// <summary>True while the control is engaged or its list is open: the mouse leaving it must not take the focus
    /// (<see cref="HoverPark"/>). Default: false.</summary>
    bool IsGamepadEngaged => false;

    /// <summary>A engaged the control (a dropdown opens its list here). Default: nothing.</summary>
    void OnGamepadEngaged()
    {
    }

    /// <summary>The engagement ended: <paramref name="confirm"/> is true for A (keep the choice), false for B or for
    /// the focus moving away (abandon an uncommitted choice). Default: nothing.</summary>
    /// <param name="confirm">Whether the choice is kept.</param>
    void OnGamepadDisengaged(bool confirm)
    {
    }
}
