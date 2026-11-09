// The kit page: one window with a tab per sample (KitSamples) and a status line that says whether this build hosts
// the DevTools and which keys open them. The page is built with the demo's style sheet so the templated buttons look
// as they do in the Overlay and the Workbench. KitMode shows it on upstream's platform host; the tests show the same
// page on the embedded host, which is why the page is a plain Window and not a platform-specific type.
//
// The content is composed inside Window.Build(...), the composition-site idiom upstream's Hot Reload rebuilds: edit
// Compose (or anything it calls in this type) under `dotnet watch` and the window re-composes on save, keeping the
// view models (constructed outside the callback) and their state. The callback must stay re-runnable, so it only
// composes; one-shot window specs (title, size, style sheet) are set once in Build().
using Aprillz.MewUI;
using Aprillz.MewUI.Controls;

using MewStage.Demo.Overlay;
using MewStage.Demo.Workbench;

namespace MewStage.Demo.Kit;

/// <summary>The kit page (see the file header).</summary>
internal sealed class KitPage
{
    /// <summary>The tab headers, in order.</summary>
    public static readonly string[] TabNames = ["Buttons", "Inspector", "HUD", "Icons", "Chart", "Geometry", "Type"];

    /// <summary>The status line when this build hosts the DevTools.</summary>
    public const string DevToolsAvailable = "DevTools: Ctrl+Shift+I inspector | Ctrl+Shift+T visual tree | Ctrl+Shift+P performance | Ctrl+Shift+Alt+P profiler | run under dotnet watch for Hot Reload";

    /// <summary>The status line when it does not (Release, trimmed or NativeAOT).</summary>
    public const string DevToolsUnavailable = "DevTools are not in this build (they need a Debug JIT build)";

    private KitPage(Window window, WorkbenchViewModel inspectorModel, HudViewModel hudModel)
    {
        Window = window;
        InspectorModel = inspectorModel;
        HudModel = hudModel;
        Tabs = new TabControl();
        Status = new TextBlock();
        Geometry = new GeometrySample();
    }

    /// <summary>The window to run.</summary>
    public Window Window { get; }

    /// <summary>The tab strip (replaced on every (re)composition).</summary>
    public TabControl Tabs { get; private set; }

    /// <summary>The status line (replaced on every (re)composition).</summary>
    public TextBlock Status { get; private set; }

    /// <summary>The geometry sample (replaced on every (re)composition).</summary>
    public GeometrySample Geometry { get; private set; }

    /// <summary>The view model behind the Inspector tab (survives re-composition).</summary>
    public WorkbenchViewModel InspectorModel { get; }

    /// <summary>The view model behind the HUD tab (survives re-composition).</summary>
    public HudViewModel HudModel { get; }

    /// <summary>Build the page.</summary>
    public static KitPage Build()
    {
        var window = new Window
        {
            Padding = Thickness.Zero,
            StyleSheet = DemoStyles.CreateSheet(),
        };
        window.Title("MewStage Kit").Resizable(1280, 800);
        var page = new KitPage(window, new WorkbenchViewModel(), new HudViewModel());
        window.Build(page.Compose);
        return page;
    }

    /// <summary>Compose the content (re-run by Hot Reload; see the file header).</summary>
    private void Compose(Window window)
    {
        var tabs = new TabControl();
        tabs.TabItems(
            new TabItem().Header(TabNames[0]).Content(KitSamples.Buttons()),
            new TabItem().Header(TabNames[1]).Content(KitSamples.Inspector(InspectorModel)),
            new TabItem().Header(TabNames[2]).Content(KitSamples.Hud(HudModel)),
            new TabItem().Header(TabNames[3]).Content(KitSamples.Icons()),
            new TabItem().Header(TabNames[4]).Content(KitSamples.Chart()),
            new TabItem().Header(TabNames[5]).Content(KitSamples.Geometry(out GeometrySample geometry)),
            new TabItem().Header(TabNames[6]).Content(KitSamples.Type()));

        TextBlock status = new TextBlock().Text(WindowDevTools.IsSupported ? DevToolsAvailable : DevToolsUnavailable).FontSize(12).TextWrapping(TextWrapping.Wrap);
        Border statusBar = new Border { Child = status.Margin(10, 6, 10, 6) }.Background(Color.FromRgb(30, 33, 41));

        Grid root = new Grid().Rows("*,Auto");
        root.Add(tabs.Row(0));
        root.Add(statusBar.Row(1));
        window.Content(root);

        Tabs = tabs;
        Status = status;
        Geometry = geometry;
    }
}
