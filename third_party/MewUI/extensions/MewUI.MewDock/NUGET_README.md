# Aprillz.MewUI.MewDock

A docking framework for [MewUI](https://github.com/aprillz/MewUI): dockable document tabs and tool panes,
drag-and-drop rearranging, splits, auto-hide edges, maximize, and cross-window popouts. A C#-idiomatic port of
[FlexLayout](https://github.com/caplin/FlexLayout) with a Visual Studio style docking layer.

```csharp
var manager = new DockingManager()
    .WithContentFactory(pane => new TextBlock { Text = pane.Title });

manager.LoadLayout(layoutJson);                 // FlexLayout-compatible JSON
manager.AddDocumentPane("Report", new ReportView());

window.Content(manager);                        // DockingManager is a Panel
```

- One control (`DockingManager`) + lightweight `DockPane` / `DockGroup` handles.
- Save / restore via `SaveLayout` / `LoadLayout`.
- Right-click tab/group menus (`TabMenuOpening` / `GroupMenuOpening` events), runtime-localizable strings.

Full docs: https://github.com/aprillz/MewUI/blob/main/extensions/MewUI.MewDock/README.md

Includes a port of FlexLayout (MIT). See `THIRD_PARTY_NOTICES.md` in this package.
