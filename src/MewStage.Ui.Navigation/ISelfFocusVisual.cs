using System.Diagnostics.CodeAnalysis;

namespace MewStage.Ui.Navigation;

/// <summary>
/// Marker for a focusable control that renders its OWN focus / selection / engaged indication. The gamepad focus
/// navigator draws no focus ring of its own (a control's focused style state is the focus indicator), so a focusable
/// without its own focused look would show nothing when focused; implementing this records that the control has one.
/// </summary>
[SuppressMessage("Design", "CA1040:Avoid empty interfaces", Justification = "A marker the navigator tests with a type check; it carries no behavior by design.")]
public interface ISelfFocusVisual
{
}
