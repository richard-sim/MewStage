namespace Aprillz.MewUI.Controls;

/// <summary>The pieces the built-in item templates share.</summary>
internal static class DefaultItemTemplates
{
    /// <summary>
    /// The template a presenter holds until its owner supplies one: a label showing the item index.
    /// </summary>
    public static IDataTemplate IndexPlaceholder { get; } = new DelegateTemplate<object?>(
        build: static _ => new TextBlock(),
        bind: static (view, _, index, _) =>
        {
            if (view is TextBlock label)
            {
                label.Text = index.ToString();
            }
        });

    /// <summary>Gives a default item label its text and the font of the control that owns the items.</summary>
    public static void SyncTextAndFont(TextBlock label, string text, Control owner)
    {
        if (label.Text != text)
        {
            label.Text = text;
        }

        if (label.FontFamily != owner.FontFamily)
        {
            label.FontFamily = owner.FontFamily;
        }

        if (!label.FontSize.Equals(owner.FontSize))
        {
            label.FontSize = owner.FontSize;
        }

        if (label.FontWeight != owner.FontWeight)
        {
            label.FontWeight = owner.FontWeight;
        }
    }
}
