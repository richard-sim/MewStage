// The stats panel: a MewCharts (LiveCharts2) line chart of the frame time over the last N frames and a bar of the
// UI's draw-call count, with a ComboBox for the range. Pushing a sample mutates the ObservableCollections the series
// observe, so the chart redraws by itself (retained rendering: the chart invalidates its own visual). Lives in the
// Workbench dock, or in a secondary OS window when detached (View > Stats Window).
using System;
using System.Collections.ObjectModel;

using Aprillz.MewUI;
using Aprillz.MewUI.Controls;
using Aprillz.MewUI.MewCharts;
using Aprillz.MewUI.MewCharts.Views;

using LiveChartsCore.Defaults;

namespace MewStage.Demo.Workbench;

/// <summary>The frame-time chart and the draw-count bar (see the file header).</summary>
internal sealed class StatsPanel
{
    /// <summary>The ranges the ComboBox offers (frames kept).</summary>
    public static readonly int[] Ranges = [60, 120, 240];

    private readonly ObservableCollection<ObservableValue> _frameTimes;
    private readonly ObservableCollection<ObservableValue> _drawCalls;

    private StatsPanel(ObservableCollection<ObservableValue> frameTimes, ObservableCollection<ObservableValue> drawCalls)
    {
        LiveChartsMewUI.EnsureInitialized();
        _frameTimes = frameTimes;
        _drawCalls = drawCalls;
        FrameChart = new CartesianChart
        {
            Series = [new LineSeries<ObservableValue>(_frameTimes) { GeometrySize = 0, LineSmoothness = 0, Name = "frame ms" }],
            XAxes = [new Axis { IsVisible = false }],
            YAxes = [new Axis { Name = "ms", MinLimit = 0 }],
        };
        DrawChart = new CartesianChart
        {
            Series = [new ColumnSeries<ObservableValue>(_drawCalls) { Name = "UI draw calls" }],
            XAxes = [new Axis { Labels = ["ui"], IsVisible = false }],
            YAxes = [new Axis { MinLimit = 0 }],
        };
        Range = new ComboBox().Items("60 frames", "120 frames", "240 frames").Width(130);
        Range.SelectedIndex = 2;

        StackPanel header = new StackPanel().Horizontal().Spacing(10);
        header.Add(new TextBlock().Text("Frame time").CenterVertical());
        header.Add(Range);
        Grid root = new Grid().Rows("Auto,*").Columns("3*,*").Spacing(6).Margin(8);
        root.Add(header.Row(0).Column(0));
        root.Add(new TextBlock().Text("UI draw calls").CenterVertical().Row(0).Column(1));
        root.Add(FrameChart.Row(1).Column(0));
        root.Add(DrawChart.Row(1).Column(1));
        Root = root;
    }

    /// <summary>The panel to dock or to show in the stats window.</summary>
    public FrameworkElement Root { get; }

    /// <summary>The frame-time line chart.</summary>
    public CartesianChart FrameChart { get; }

    /// <summary>The draw-count bar chart.</summary>
    public CartesianChart DrawChart { get; }

    /// <summary>The range ComboBox (opens inside whichever surface the panel is on).</summary>
    public ComboBox Range { get; }

    /// <summary>Frames kept in the line chart.</summary>
    public int Kept => Ranges[Math.Clamp(Range.SelectedIndex, 0, Ranges.Length - 1)];

    /// <summary>Samples pushed so far.</summary>
    public long Samples { get; private set; }

    /// <summary>Points in the line right now.</summary>
    public int PointCount => _frameTimes.Count;

    /// <summary>Build the panel; <paramref name="initialSamples"/> zero samples seed the line so the chart has a shape from frame one.</summary>
    public static StatsPanel Build(int initialSamples = 0)
    {
        var times = new ObservableCollection<ObservableValue>();
        for (int i = 0; i < initialSamples; i++)
        {
            times.Add(new ObservableValue(0));
        }

        return new StatsPanel(times, [new ObservableValue(0)]);
    }

    /// <summary>Record one frame.</summary>
    public void Push(double frameMilliseconds, int drawCalls)
    {
        _frameTimes.Add(new ObservableValue(frameMilliseconds));
        int kept = Kept;
        while (_frameTimes.Count > kept)
        {
            _frameTimes.RemoveAt(0);
        }

        _drawCalls[0].Value = drawCalls;
        Samples++;
    }
}
