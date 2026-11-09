// The kit page's tabs, one builder each: the demo's templated buttons beside upstream's stock controls, the
// Workbench inspector over a stand-alone view model, the HUD panel at 1:1, the SVG icon set, a MewCharts chart over
// synthetic data, the geometry sample and the two typefaces. Every builder returns a plain element so the tabs are
// also usable on the embedded host (the headless tests build them there).
using System;
using System.Collections.ObjectModel;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.MewCharts;
using Aprillz.MewUI.MewCharts.Views;

using LiveChartsCore.Defaults;

using MewStage.Demo.Overlay;
using MewStage.Demo.Workbench;

namespace MewStage.Demo.Kit;

/// <summary>The kit page's tab contents (see the file header).</summary>
internal static class KitSamples
{
    /// <summary>Points in the chart tab's synthetic series.</summary>
    public const int ChartPoints = 48;

    private static readonly Color Dim = Color.FromRgb(176, 182, 196);

    /// <summary>The demo buttons in each state the style sheet distinguishes statically, and upstream's stock controls.</summary>
    public static FrameworkElement Buttons()
    {
        StackPanel demo = new StackPanel().Vertical().Spacing(12);
        demo.Add(Caption("DemoButton: a templated Button; the named styles animate the chrome through StateTriggers (hover, press, focus, disabled)"));
        StackPanel primary = new StackPanel().Horizontal().Spacing(12);
        primary.Add(DemoButton.Create(DemoStyles.Primary, "Primary"));
        primary.Add(DemoButton.Create(DemoStyles.Primary, "Primary (disabled)").IsEnabled(false));
        primary.Add(DemoButton.Create(DemoStyles.Ghost, "Ghost"));
        primary.Add(DemoButton.Create(DemoStyles.Ghost, "Ghost (disabled)").IsEnabled(false));
        demo.Add(primary);

        demo.Add(Caption("Stock controls (upstream's default theme, dark variant)").Margin(0, 16, 0, 0));
        StackPanel row1 = new StackPanel().Horizontal().Spacing(12);
        row1.Add(new Button().Content("Button"));
        row1.Add(new Button().Content("Disabled").IsEnabled(false));
        row1.Add(new CheckBox().Content("CheckBox").IsChecked());
        row1.Add(new RadioButton().Content("One").GroupName("kit").IsChecked());
        row1.Add(new RadioButton().Content("Two").GroupName("kit"));
        row1.Add(new ToggleSwitch().Content("ToggleSwitch").IsChecked());
        demo.Add(row1);
        StackPanel row2 = new StackPanel().Horizontal().Spacing(12);
        row2.Add(new Slider().Minimum(0).Maximum(100).Value(35).Width(180).CenterVertical());
        row2.Add(new ProgressBar().Minimum(0).Maximum(100).Value(65).Width(180).CenterVertical());
        row2.Add(new NumericUpDown().Minimum(0).Maximum(10).Value(3).Width(100));
        row2.Add(new TextBox().Text("TextBox").Width(160));
        row2.Add(new ComboBox().Items("Alpha", "Beta", "Gamma").SelectedIndex(0).Width(140));
        demo.Add(row2);
        Expander expander = new Expander().Header("Expander").IsExpanded(true);
        expander.Content(new TextBlock().Text("Content inside an Expander, in a GroupBox.").Margin(8));
        GroupBox group = new GroupBox().Header("GroupBox").Width(420);
        group.Content(expander);
        demo.Add(group);
        return demo.Margin(16);
    }

    /// <summary>The Workbench outliner and inspector over a view model of their own (no scene behind it).</summary>
    public static FrameworkElement Inspector(WorkbenchViewModel vm)
    {
        Grid grid = new Grid().Columns("220,*").Spacing(16).Margin(16);
        ListBox outliner = Outliner.Build(vm);
        grid.Add(outliner.Column(0).Top().Height(160));
        var inspector = Workbench.Inspector.Build(vm);
        grid.Add(inspector.Root.Column(1).Top());
        return grid;
    }

    /// <summary>The HUD's 1920 x 1080 panel at 1:1 on its world background, inside a scroller.</summary>
    public static FrameworkElement Hud(HudViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        vm.SetReadouts(16.7, 0.785f);
        Grid root = HudView.BuildRoot(vm, out _, out _, out _);
        var backdrop = new Border { Background = Color.FromRgb(19, 22, 28), Child = root };
        return new ScrollViewer().Scroll(ScrollMode.Auto, ScrollMode.Auto).Content(backdrop);
    }

    /// <summary>Every SVG icon at three sizes.</summary>
    public static FrameworkElement Icons()
    {
        StackPanel root = new StackPanel().Vertical().Spacing(16).Margin(16);
        root.Add(Caption("assets/icons/*.svg through MewUI.Svg (SvgImageSource), rasterized once per size at 24, 36 and 64 DIP (Image.VectorCacheMode Cached)"));
        foreach (double size in new[] { 24.0, 36.0, 64.0 })
        {
            StackPanel row = new StackPanel().Horizontal().Spacing(18);
            foreach (string name in DemoIcons.Names)
            {
                StackPanel cell = new StackPanel().Vertical().Spacing(4);
                if (DemoIcons.Icon(name, size) is { } image)
                {
                    cell.Add(image.CenterHorizontal());
                }

                cell.Add(new TextBlock().Text(name).FontSize(12).Foreground(Dim).CenterHorizontal());
                row.Add(cell);
            }

            root.Add(row);
        }

        return root;
    }

    /// <summary>A line and a column series over synthetic data (MewCharts / LiveCharts2).</summary>
    public static FrameworkElement Chart()
    {
        LiveChartsMewUI.EnsureInitialized();
        var wave = new ObservableCollection<ObservableValue>();
        var bars = new ObservableCollection<ObservableValue>();
        for (int i = 0; i < ChartPoints; i++)
        {
            double t = i / (double)ChartPoints;
            wave.Add(new ObservableValue(50 + (40 * Math.Sin(t * Math.Tau)) + (8 * Math.Sin(t * Math.Tau * 5))));
            if (i % 6 == 0)
            {
                bars.Add(new ObservableValue(20 + (i * 7 % 60)));
            }
        }

        var line = new CartesianChart
        {
            Series = [new LineSeries<ObservableValue>(wave) { GeometrySize = 0, LineSmoothness = 0.6f, Name = "wave" }],
            XAxes = [new Axis { IsVisible = false }],
            YAxes = [new Axis { MinLimit = 0, MaxLimit = 100 }],
        };
        var columns = new CartesianChart
        {
            Series = [new ColumnSeries<ObservableValue>(bars) { Name = "columns" }],
            XAxes = [new Axis { IsVisible = false }],
            YAxes = [new Axis { MinLimit = 0 }],
        };
        Grid grid = new Grid().Rows("Auto,*").Columns("3*,2*").Spacing(12).Margin(16);
        grid.Add(Caption("MewCharts (LiveCharts2 on MewUI): synthetic series; the Workbench's stats panel feeds the same chart types live").Row(0).Column(0));
        grid.Add(line.Row(1).Column(0));
        grid.Add(columns.Row(1).Column(1));
        return grid;
    }

    /// <summary>The geometry sample with a label under each cell.</summary>
    public static FrameworkElement Geometry(out GeometrySample sample)
    {
        StackPanel root = new StackPanel().Vertical().Spacing(8).Margin(16);
        root.Add(Caption("MewUI.Geometry: PathGeometryOperations.Combine of a rectangle and an ellipse (CPU, backend independent)"));
        sample = new GeometrySample();
        root.Add(sample.Left());
        StackPanel labels = new StackPanel().Horizontal();
        foreach (Aprillz.MewUI.Geometry.GeometryCombineMode mode in GeometrySample.Modes)
        {
            labels.Add(new TextBlock().Text(mode.ToString()).Foreground(Dim).Width(GeometrySample.Cell));
        }

        root.Add(labels);
        return root;
    }

    /// <summary>Roboto and Lora at several sizes and the faces each family ships.</summary>
    public static FrameworkElement Type()
    {
        StackPanel root = new StackPanel().Vertical().Spacing(10).Margin(16);
        root.Add(Caption("Roboto (body, embedded in MewStage.Ui.Backend) and Lora (display, assets/fonts), the same files both hosts load"));
        foreach (double size in new[] { 14.0, 20.0, 32.0 })
        {
            root.Add(new TextBlock().Text("Roboto Regular " + size + " - The quick brown fox jumps over the lazy dog").FontFamily(DemoFonts.Body).FontSize(size));
        }

        root.Add(new Separator().Margin(0, 8, 0, 8));
        foreach (double size in new[] { 20.0, 32.0 })
        {
            root.Add(new TextBlock().Text("Lora Regular " + size + " - Sphinx of black quartz, judge my vow").FontFamily(DemoFonts.Display).FontSize(size));
            root.Add(new TextBlock().Text("Lora Italic " + size + " - Sphinx of black quartz, judge my vow").FontFamily(DemoFonts.Display).FontSize(size).Italic());
            root.Add(new TextBlock().Text("Lora Bold " + size + " - Sphinx of black quartz, judge my vow").FontFamily(DemoFonts.Display).FontSize(size).Bold());
            root.Add(new TextBlock().Text("Lora Bold Italic " + size + " - Sphinx of black quartz, judge my vow").FontFamily(DemoFonts.Display).FontSize(size).Bold().Italic());
        }

        return root;
    }

    private static TextBlock Caption(string text)
    {
        return new TextBlock().Text(text).Foreground(Dim).TextWrapping(TextWrapping.Wrap);
    }
}
