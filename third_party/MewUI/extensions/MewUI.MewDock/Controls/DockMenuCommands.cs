using Aprillz.MewUI.Controls;

namespace Aprillz.MewUI.MewDock.Controls;

internal static class DockMenuCommands
{
    public static void Add(ContextMenu menu, CommandScope commands, string id, string text, Action execute, bool canExecute = true)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(execute);

        var command = new Command($"mewdock.menu.{id}", text);
        commands.Register(command, execute, () => canExecute);
        menu.AddItem(command);
    }
}
