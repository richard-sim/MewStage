// The outliner: a ListBox over the scene objects with a typed item template (an icon and a name per row); its
// selection index is bound two-way into the view model, which the inspector follows.
using System;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

namespace MewStage.Demo.Workbench;

/// <summary>The scene-object list (see the file header).</summary>
internal static class Outliner
{
    /// <summary>Build the list over <paramref name="vm"/>.</summary>
    public static ListBox Build(WorkbenchViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        ListBox list = new ListBox()
            .ItemsSource(ItemsView.Create(WorkbenchViewModel.Objects, o => o.Name))
            .ItemTemplate<SceneObject, Row>(static _ => new Row(), static (row, item) => row.Show(item))
            .BindSelectedIndex(vm.SelectedIndex);
        list.ItemHeight(30);
        return list;
    }

    /// <summary>One outliner row: an icon and the object's name.</summary>
    private sealed class Row : StackPanel
    {
        private readonly Image _icon = new Image().Size(18).CenterVertical();
        private readonly TextBlock _name = new TextBlock().CenterVertical();

        public Row()
        {
            this.Horizontal().Spacing(10).Padding(6, 0);
            Add(_icon);
            Add(_name);
        }

        public void Show(SceneObject item)
        {
            _icon.Source = DemoIcons.Get(item.Icon);
            _name.Text = item.Name;
        }
    }
}
