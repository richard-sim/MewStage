namespace Aprillz.MewUI.Controls;

public interface IDataTemplate
{
    FrameworkElement Build(TemplateContext context);

    void Bind(FrameworkElement view, object? item, int index, TemplateContext context);

    void Unbind(FrameworkElement view, object? item, int index, TemplateContext context)
    {
    }
}

/// <summary>
/// A template for items of one type. Implement the typed members; the untyped ones forward to them.
/// </summary>
/// <remarks>
/// A null item reaches the typed members as <c>default</c>, and an item of another type throws
/// <see cref="InvalidCastException"/>.
/// </remarks>
public interface IDataTemplate<in TItem> : IDataTemplate
{
    void Bind(FrameworkElement view, TItem item, int index, TemplateContext context);

    void Unbind(FrameworkElement view, TItem item, int index, TemplateContext context)
    {
    }

    void IDataTemplate.Bind(FrameworkElement view, object? item, int index, TemplateContext context)
        => Bind(view, CastItem(item), index, context);

    void IDataTemplate.Unbind(FrameworkElement view, object? item, int index, TemplateContext context)
        => Unbind(view, CastItem(item), index, context);

    private static TItem CastItem(object? item)
    {
        if (item is null)
        {
            return default!;
        }

        if (item is not TItem typed)
        {
            throw new InvalidCastException($"Template expected item type '{typeof(TItem).Name}', but got '{item.GetType().Name}'.");
        }

        return typed;
    }
}
