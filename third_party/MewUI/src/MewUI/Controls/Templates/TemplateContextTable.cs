using System.Runtime.CompilerServices;

namespace Aprillz.MewUI.Controls;

/// <summary>
/// Keeps the one <see cref="TemplateContext"/> each built container owns, for a realization engine
/// that builds, binds and recycles containers through an <see cref="IDataTemplate"/>.
/// </summary>
internal sealed class TemplateContextTable
{
    // Weak, so a container dropped from the pool takes its context with it.
    private readonly ConditionalWeakTable<FrameworkElement, TemplateContext> _contexts = new();

    public FrameworkElement Build(IDataTemplate template)
    {
        var context = new TemplateContext();
        var view = template.Build(context);
        _contexts.Add(view, context);
        return view;
    }

    public void Bind(FrameworkElement view, IDataTemplate template, object? item, int index)
        => _contexts.GetValue(view, static _ => new TemplateContext()).BindTemplate(view, template, item, index);

    public void Unbind(FrameworkElement view)
    {
        if (_contexts.TryGetValue(view, out var context))
        {
            context.UnbindTemplate(view);
        }
    }
}
