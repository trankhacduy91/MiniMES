using System.Globalization;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Quality;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace MiniMes.Client.Core.ViewModels.Quality;

// One drawn point: X = sample number in the window, Y = value, Text = the tooltip (time, value, serial, rules).
public sealed record ChartPoint(double X, double Y, string Text);

// Spec M7 2.4: the I chart and, under it, the [v2] MR chart of one plan, as plain OxyPlot.Core models (unit tested,
// like the M5 charts; the WPF PlotView only draws them). X = the sample number in the window, oldest first; the
// tooltip shows the measuring time on the plant clock and the serial.
//   Control limits (the process, from data): red, dashed.  Spec limits (the customer): black, solid, thicker.
//   Centre line: green.  Points that fired a rule: red, bigger, drawn on top.
// Colour is never the only difference (spec 1.5 mistake 1, colour-blind readers): style, width and labels too.
public static class ControlChartBuilder
{
    public static readonly OxyColor PointColor = OxyColor.FromRgb(0x2B, 0x6C, 0xB0);
    public static readonly OxyColor ViolationColor = OxyColors.Red;
    public static readonly OxyColor ControlLimitColor = OxyColor.FromRgb(0xC0, 0x39, 0x2B);
    public static readonly OxyColor SpecLimitColor = OxyColors.Black;
    public static readonly OxyColor CentreColor = OxyColor.FromRgb(0x2E, 0x7D, 0x32);

    // Same value as ControlLimitSet.D4 / SpcCalculator.D4 (UCL_MR = 3.267 × MR̄), for the window's own MR̄.
    public const double D4 = 3.267;

    public static PlotModel IChart(SpcChartDto chart, Func<DateTime, DateTime> toPlant)
    {
        var model = new PlotModel { Title = $"{chart.PlanCode} ({chart.Unit})", TitleFontSize = 14 };
        var rulesAt = chart.Violations.GroupBy(v => v.MeasurementId).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(v => v.Rule)));
        var points = chart.Points.Select((p, i) => new ChartPoint(i + 1, (double)p.Value,
            Tooltip(chart, p, toPlant, rulesAt.GetValueOrDefault(p.MeasurementId)))).ToList();

        model.Series.Add(new LineSeries
        {
            Color = PointColor, MarkerType = MarkerType.Circle, MarkerSize = 3, MarkerFill = PointColor,
            ItemsSource = points, Mapping = o => new DataPoint(((ChartPoint)o).X, ((ChartPoint)o).Y), TrackerFormatString = "{Text}",
        });
        // Spec 2.4 trap 3: painted by the point's place in THIS window (the server sends measurement ids).
        model.Series.Add(new ScatterSeries
        {
            MarkerType = MarkerType.Circle, MarkerSize = 6, MarkerFill = ViolationColor, MarkerStroke = OxyColors.White,
            MarkerStrokeThickness = 1, TrackerFormatString = "{Text}",
            ItemsSource = points.Where((_, i) => rulesAt.ContainsKey(chart.Points[i].MeasurementId)).ToList(),
            Mapping = o => new ScatterPoint(((ChartPoint)o).X, ((ChartPoint)o).Y),
        });

        var lines = new List<LineAnnotation>();
        if (chart.Limits is { } l)
        {
            lines.Add(Horizontal((double)l.Mean, $"CL {Fmt(l.Mean)}", CentreColor, LineStyle.Solid, 1));
            lines.Add(Horizontal((double)l.Ucl, $"UCL {Fmt(l.Ucl)}", ControlLimitColor, LineStyle.Dash, 1.5));
            lines.Add(Horizontal((double)l.Lcl, $"LCL {Fmt(l.Lcl)}", ControlLimitColor, LineStyle.Dash, 1.5));
        }
        if (chart.Usl is { } usl) lines.Add(Horizontal((double)usl, $"USL {Fmt(usl)}", SpecLimitColor, LineStyle.Solid, 2));
        if (chart.Lsl is { } lsl) lines.Add(Horizontal((double)lsl, $"LSL {Fmt(lsl)}", SpecLimitColor, LineStyle.Solid, 2));
        foreach (var line in lines) model.Annotations.Add(line);

        // The axis shows every line, not only the data (2.4 trap 1): otherwise USL/LSL fall outside the frame and
        // nobody sees how far the process is from the customer's limits.
        AddAxes(model, points.Select(p => p.Y).Concat(lines.Select(x => x.Y)).ToList(), points.Count, chart.Unit, minimumZero: false);
        return model;
    }

    // [v2] The moving range |x(i) − x(i−1)| under the I chart, same X. MR̄ and UCL_MR from the baseline when it has
    // them; a seeded baseline has none (no data behind it), then the window's own MR̄ is drawn and labelled so.
    public static PlotModel MrChart(SpcChartDto chart)
    {
        var model = new PlotModel { Title = Strings.Spc_MrTitle, TitleFontSize = 12 };
        var points = chart.Points.Select((p, i) => (p, i)).Where(x => x.p.MovingRange is not null)
            .Select(x => new ChartPoint(x.i + 1, x.p.MovingRange!.Value,
                string.Create(CultureInfo.InvariantCulture, $"#{x.i + 1}  MR {x.p.MovingRange!.Value:0.######}"))).ToList();
        model.Series.Add(new LineSeries
        {
            Color = PointColor, MarkerType = MarkerType.Circle, MarkerSize = 2.5, MarkerFill = PointColor,
            ItemsSource = points, Mapping = o => new DataPoint(((ChartPoint)o).X, ((ChartPoint)o).Y), TrackerFormatString = "{Text}",
        });

        var ys = points.Select(p => p.Y).Append(0).ToList();
        double? mrBar = chart.Limits?.MrBar is { } b ? (double)b : chart.Window?.MrBar;
        if (mrBar is { } bar)
        {
            var fromBaseline = chart.Limits?.MrBar is not null;
            var ucl = fromBaseline && chart.Limits!.UclMr is { } u ? (double)u : D4 * bar;
            var suffix = fromBaseline ? "" : " " + Strings.Spc_FromWindow;
            model.Annotations.Add(Horizontal(bar, string.Create(CultureInfo.InvariantCulture, $"MR̄ {bar:0.####}{suffix}"), CentreColor, LineStyle.Solid, 1));
            model.Annotations.Add(Horizontal(ucl, string.Create(CultureInfo.InvariantCulture, $"UCL_MR {ucl:0.####}{suffix}"), ControlLimitColor, LineStyle.Dash, 1.5));
            ys.Add(ucl);
        }
        AddAxes(model, ys, chart.Points.Count, chart.Unit, minimumZero: true);
        return model;
    }

    private static LineAnnotation Horizontal(double y, string text, OxyColor color, LineStyle style, double width) => new()
    {
        Type = LineAnnotationType.Horizontal, Y = y, Text = text, Color = color, LineStyle = style, StrokeThickness = width,
        TextColor = color, TextHorizontalAlignment = HorizontalAlignment.Right, TextVerticalAlignment = VerticalAlignment.Bottom,
    };

    private static void AddAxes(PlotModel model, IReadOnlyCollection<double> ys, int count, string unit, bool minimumZero)
    {
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom, Title = Strings.Spc_SampleAxis, Minimum = 0.5, Maximum = Math.Max(count, 1) + 0.5,
            MajorStep = count > 50 ? 10 : count > 20 ? 5 : 1, MinorStep = 1, IsZoomEnabled = false, IsPanEnabled = false,
        });
        var min = ys.Count == 0 ? 0 : ys.Min();
        var max = ys.Count == 0 ? 1 : ys.Max();
        var pad = Math.Max((max - min) * 0.08, 1e-6);
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left, Title = unit, Minimum = minimumZero ? 0 : min - pad, Maximum = max + pad,
            IsZoomEnabled = false, IsPanEnabled = false,
        });
    }

    private static string Tooltip(SpcChartDto chart, SpcPointDto p, Func<DateTime, DateTime> toPlant, string? rules)
        => string.Create(CultureInfo.InvariantCulture, $"{toPlant(p.MeasuredAt):dd/MM HH:mm:ss}  {Fmt(p.Value)} {chart.Unit}  {p.Result}")
           + (p.SerialNo is { } s ? $"\n{s}" : "") + (rules is null ? "" : $"\n{rules}");

    private static string Fmt(decimal d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}
