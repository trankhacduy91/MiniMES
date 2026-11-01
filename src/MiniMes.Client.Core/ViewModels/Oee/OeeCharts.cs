using CommunityToolkit.Mvvm.ComponentModel;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Oee;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;

namespace MiniMes.Client.Core.ViewModels.Oee;

// Spec M5 2.6. A PlotModel is plain data (OxyPlot.Core, no WPF type), so it lives in the ViewModel and is unit-tested
// on Linux; the WPF PlotView only draws it. Each Load builds a new model and assigns it: the view redraws through the
// binding, no InvalidatePlot needed (the price: a zoom is lost on refresh, fine for a 15 s dashboard).
//
// OxyPlot 2.2 has no ColumnSeries (removed in 2.1): a vertical bar is a BarSeries whose category axis is at the
// bottom. BarSeries reads its categories on its Y axis and values on its X axis, so XAxisKey = the value axis (left)
// and YAxisKey = the category axis (bottom): the series is then "transposed" and draws columns.
internal static class Charts
{
    public const string CategoryKey = "cat";
    public const string ValueKey = "value";
    public const string PercentKey = "pct";

    // Not only red/green (colour-blind readers): the line is also dashed and has markers.
    public static readonly OxyColor Bars = OxyColor.FromRgb(0x2B, 0x6C, 0xB0);
    public static readonly OxyColor LineColor = OxyColor.FromRgb(0xC0, 0x39, 0x2B);

    // Tooltip on hover (the spec's "move the mouse over a column, see the minutes"), not only on click. Also a plain
    // OxyPlot.Core type: the view binds PlotView.Controller to it.
    public static PlotController HoverController()
    {
        var controller = new PlotController();
        controller.UnbindAll();
        controller.BindMouseEnter(PlotCommands.HoverSnapTrack);
        return controller;
    }

    public static PlotModel Model(string title, IEnumerable<string> categories, string valueTitle)
    {
        var model = new PlotModel { Title = title, TitleFontSize = 14 };
        var axis = new CategoryAxis { Position = AxisPosition.Bottom, Key = CategoryKey, IsZoomEnabled = false, IsPanEnabled = false };
        axis.Labels.AddRange(categories);
        model.Axes.Add(axis);
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Left, Key = ValueKey, Minimum = 0, MinimumPadding = 0,
            MaximumPadding = 0.1, Title = valueTitle, IsZoomEnabled = false, IsPanEnabled = false });
        model.Legends.Add(new Legend { LegendPosition = LegendPosition.TopRight, LegendPlacement = LegendPlacement.Inside });
        return model;
    }

    public static BarSeries Columns(string title, string tracker)
        => new() { Title = title, FillColor = Bars, XAxisKey = ValueKey, YAxisKey = CategoryKey, TrackerFormatString = tracker };

    public static LineSeries Line(string title, string yAxisKey, string tracker) => new()
    {
        Title = title, Color = LineColor, XAxisKey = CategoryKey, YAxisKey = yAxisKey, LineStyle = LineStyle.Dash,
        MarkerType = MarkerType.Circle, MarkerSize = 3, MarkerFill = LineColor, TrackerFormatString = tracker,
    };
}

// Pareto top 5 (spec 1.2.9): columns biggest first, cumulative % on a second axis fixed at 0-100.
public partial class ParetoViewModel : ObservableObject
{
    public const int Top = 5;

    [ObservableProperty]
    private PlotModel _plotModel = new();

    public PlotController Controller { get; } = Charts.HoverController();

    public IReadOnlyList<ParetoRowDto> Rows { get; private set; } = [];

    public void Load(ParetoDto? pareto)
    {
        Rows = pareto?.Rows.Take(Top).ToList() ?? [];
        var byCount = pareto?.By == ParetoBy.Count;
        var model = Charts.Model(Strings.Oee_ParetoTitle, Rows.Select(r => r.Reason), byCount ? Strings.Oee_Stops : Strings.Oee_Minutes);
        // A second value axis on the right, locked to 0-100: otherwise every curve looks the same.
        model.Axes.Add(new LinearAxis { Position = AxisPosition.Right, Key = Charts.PercentKey, Minimum = 0, Maximum = 100,
            Title = Strings.Oee_CumulativePercent, IsZoomEnabled = false, IsPanEnabled = false });

        var bars = Charts.Columns(byCount ? Strings.Oee_Stops : Strings.Oee_Minutes, "{1}: {2:0.#}");
        bars.Items.AddRange(Rows.Select(r => new BarItem(byCount ? r.Count : r.Minutes)));
        var line = Charts.Line(Strings.Oee_CumulativePercent, Charts.PercentKey, "{0}: {4:0.#} %");
        line.Points.AddRange(Rows.Select((r, i) => new DataPoint(i, r.CumulativePercent)));   // x = category index
        model.Series.Add(bars);
        model.Series.Add(line);
        PlotModel = model;
    }
}

// Good boards per hour against the target the ideal cycle time allows (spec 2.6): planned seconds of the hour /
// ideal cycle time. 60 planned minutes at 20 s = 180; an hour with a 15-minute break = 135; a lunch hour = 0.
public partial class HourlyOutputViewModel : ObservableObject
{
    [ObservableProperty]
    private PlotModel _plotModel = new();

    public PlotController Controller { get; } = Charts.HoverController();

    public IReadOnlyList<(string Hour, long Good, double Target)> Points { get; private set; } = [];

    public void Load(IReadOnlyList<HourOeeDto> hours)
    {
        var shiftCycle = CycleTime(hours.Sum(h => h.Figures.IdealSeconds), hours.Sum(h => h.Figures.TotalCount));
        Points = hours.Select(h => (h.Hour.ToString("00", System.Globalization.CultureInfo.InvariantCulture),
            h.Figures.GoodCount, Target(h.Figures, shiftCycle))).ToList();

        var model = Charts.Model(Strings.Oee_HourlyTitle, Points.Select(p => p.Hour), Strings.Oee_Boards);
        var bars = Charts.Columns(Strings.Oee_Good, "{1}:00 {2:0}");
        bars.Items.AddRange(Points.Select(p => new BarItem(p.Good)));
        var target = Charts.Line(Strings.Oee_Target, Charts.ValueKey, "{0}: {4:0}");
        target.Points.AddRange(Points.Select((p, i) => new DataPoint(i, p.Target)));
        model.Series.Add(bars);
        model.Series.Add(target);
        PlotModel = model;
    }

    // The hour's own cycle time (several WOs: the weighted one, Ideal / Total); an hour the machine counted nothing
    // uses the shift's, or the target would vanish exactly when the machine stood still.
    public static double Target(OeeFiguresDto hour, double? shiftCycleSec)
        => (CycleTime(hour.IdealSeconds, hour.TotalCount) ?? shiftCycleSec) is { } cycle and > 0 ? hour.PlannedSeconds / cycle : 0;

    private static double? CycleTime(double idealSeconds, long count) => count > 0 ? idealSeconds / count : null;
}

// One of the big A / P / Q / OEE numbers. Null = undefined ("—"), never 0 %.
public sealed class OeeCardViewModel(string label)
{
    public string Label { get; } = label;
    public double? Value { get; private set; }
    public string Text => Value is { } v ? v.ToString("0.0 %", System.Globalization.CultureInfo.InvariantCulture) : "—";
    public double Bar => Math.Clamp((Value ?? 0) * 100, 0, 100);   // a P of 133 % still fills the bar, the text says 133

    public OeeCardViewModel With(double? value) => new(Label) { Value = value };
}
