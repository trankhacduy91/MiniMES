using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.ViewModels.Quality;
using MiniMes.Contracts.Quality;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace MiniMes.UnitTests.Client;

internal sealed class FakeQualityApi : IQualityApi
{
    public List<InspectionPlanDto> Plans { get; } = [];
    public Dictionary<int, SpcChartDto> Charts { get; } = [];
    public List<(int PlanId, int Take)> ChartCalls { get; } = [];

    public Task<IReadOnlyList<InspectionPlanDto>> GetPlansAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<InspectionPlanDto>>(Plans.ToList());

    public Task<SpcChartDto> GetChartAsync(int planId, int take, CancellationToken ct = default)
    {
        ChartCalls.Add((planId, take));
        return Task.FromResult(Charts[planId]);
    }
}

// Spec M7 step 7: the I-MR chart (ControlChartBuilder) and the screen around it (ControlChartViewModel).
public class ControlChartViewModelTests
{
    private static readonly DateTime T0 = new(2026, 11, 4, 1, 0, 0, DateTimeKind.Utc);   // 08:00 plant time

    // Spec 1.2 WE4 table: 8 points above the centre line; the run fires at the 7th and the 8th.
    private static readonly decimal[] Run8 = [3.318m, 3.325m, 3.311m, 3.322m, 3.309m, 3.330m, 3.316m, 3.321m];

    private static readonly ControlLimitsDto Seeded = new(1, 3.302m, 0.040m, 3.422m, 3.182m, null, null, 0, null, null,
        "Initial baseline (spec M7 1.2)", "seed", T0.AddDays(-1));

    private static readonly InspectionPlanDto Rail = new(1, "3V3_RAIL", "3.3 V rail voltage", "V", 3.135m, 3.465m, 3.3m, "PCB-A", "TEST", null, true);
    private static readonly InspectionPlanDto Oven = new(2, "REFLOW_PEAK_TEMP", "Reflow peak", "°C", 235m, 255m, 245m, null, null, "L1-REFLOW-01/peakTemp", true);

    private static SpcChartDto RunChart(ControlLimitsDto? limits = null, IReadOnlyList<SpcAlertDto>? alerts = null)
    {
        var points = Run8.Select((v, i) => new SpcPointDto(100 + i, T0.AddMinutes(i), v, "Ok", $"PCBA{i}",
            i == 0 ? null : Math.Abs((double)(v - Run8[i - 1])))).ToList();
        return new SpcChartDto(1, "3V3_RAIL", "3.3 V rail voltage", "V", 3.135m, 3.465m, 3.3m, limits ?? Seeded, points,
            [new(106, "WE4"), new(107, "WE4")], new SpcWindowStatsDto(8, 3.31975, 0.0068, 0.0103, 8.09, 3.53), alerts ?? []);
    }

    private static DateTime Plant(DateTime utc) => utc.AddHours(7);

    private static List<ChartPoint> Items(Series s) => (s switch
    {
        LineSeries l => l.ItemsSource,
        ScatterSeries sc => sc.ItemsSource,
        _ => throw new InvalidOperationException(),
    })!.Cast<ChartPoint>().ToList();

    private static LineAnnotation Line(PlotModel m, string prefix)
        => m.Annotations.OfType<LineAnnotation>().Single(a => a.Text?.StartsWith(prefix + " ", StringComparison.Ordinal) == true);

    // Spec check: the drift scenario shows a red dot at the 7th point of the run (and at the 8th).
    [Fact]
    public void I_chart_paints_the_points_that_fired_a_rule_red_at_their_place_in_the_window()
    {
        var model = ControlChartBuilder.IChart(RunChart(), Plant);

        Assert.Equal(Run8.Select(v => (double)v), Items(model.Series[0]).Select(p => p.Y));
        Assert.Equal([7d, 8d], Items(model.Series[1]).Select(p => p.X));
        Assert.Equal(ControlChartBuilder.ViolationColor, ((ScatterSeries)model.Series[1]).MarkerFill);
        Assert.Contains("WE4", Items(model.Series[1])[0].Text);
    }

    // Spec check: USL/LSL look different from UCL/LCL (colour AND style AND width, spec 1.5 mistake 1).
    [Fact]
    public void Spec_limits_and_control_limits_are_drawn_differently()
    {
        var model = ControlChartBuilder.IChart(RunChart(), Plant);
        var (ucl, lcl, usl, lsl, cl) = (Line(model, "UCL"), Line(model, "LCL"), Line(model, "USL"), Line(model, "LSL"), Line(model, "CL"));

        Assert.Equal((3.422, 3.182, 3.465, 3.135, 3.302), (ucl.Y, lcl.Y, usl.Y, lsl.Y, cl.Y));
        Assert.Equal((LineStyle.Dash, ControlChartBuilder.ControlLimitColor), (ucl.LineStyle, ucl.Color));
        Assert.Equal((LineStyle.Solid, ControlChartBuilder.SpecLimitColor), (usl.LineStyle, usl.Color));
        Assert.NotEqual(ucl.Color, usl.Color);
        Assert.True(usl.StrokeThickness > ucl.StrokeThickness);
        Assert.Equal(("UCL 3.422", "USL 3.465"), (ucl.Text, usl.Text));
    }

    // 2.4 trap 1: the data sits around 3.31-3.33, the axis still shows USL and LSL.
    [Fact]
    public void The_value_axis_shows_the_spec_limits_not_only_the_data()
    {
        var axis = ControlChartBuilder.IChart(RunChart(), Plant).Axes.Single(a => a.Position == AxisPosition.Left);
        Assert.True(axis.Minimum < 3.135 && axis.Maximum > 3.465, $"{axis.Minimum}..{axis.Maximum}");
    }

    // No baseline yet: points and spec limits only, no control lines.
    [Fact]
    public void Without_a_baseline_only_the_spec_limits_are_drawn()
    {
        var chart = RunChart() with { Limits = null, Violations = [] };
        var texts = ControlChartBuilder.IChart(chart, Plant).Annotations.OfType<LineAnnotation>().Select(a => a.Text!.Split(' ')[0]);
        Assert.Equal(["USL", "LSL"], texts);
    }

    [Fact]
    public void The_tooltip_shows_plant_time_value_serial_and_rules()
        => Assert.Equal("04/11 08:06:00  3.316 V  Ok\nPCBA6\nWE4", Items(ControlChartBuilder.IChart(RunChart(), Plant).Series[0])[6].Text);

    // [v2] Spec check: the MR chart has MR̄ and UCL_MR. A seeded baseline has no MR̄: the window's own, said so.
    [Fact]
    public void Mr_chart_draws_the_moving_ranges_with_the_window_mr_bar_when_the_baseline_has_none()
    {
        var model = ControlChartBuilder.MrChart(RunChart());

        var points = Items(model.Series[0]);
        Assert.Equal(2d, points[0].X);                              // the first point has no previous one
        Assert.Equal(7, points.Count);
        Assert.Equal(0.007, points[0].Y, precision: 6);             // |3.325 − 3.318|
        Assert.Equal((0.0103, 3.267 * 0.0103), (Line(model, "MR̄").Y, Line(model, "UCL_MR").Y));
        Assert.EndsWith("(this window)", Line(model, "MR̄").Text);
    }

    [Fact]
    public void Mr_chart_uses_the_baseline_mr_bar_when_it_has_one()
    {
        var model = ControlChartBuilder.MrChart(RunChart(Seeded with { MrBar = 0.0544m, UclMr = 0.1777m }));

        Assert.Equal((0.0544, 0.1777), (Line(model, "MR̄").Y, Line(model, "UCL_MR").Y));
        Assert.Equal("MR̄ 0.0544", Line(model, "MR̄").Text);
        Assert.Equal(0, model.Axes.Single(a => a.Position == AxisPosition.Left).Minimum);
    }

    // ---------- the screen ----------

    private readonly FakeQualityApi _api = new();
    private readonly FakeRealtimeFactory _rt = new();
    private readonly FakeTimeProvider _time = new(T0);
    private readonly RecordingPages _pages = new();

    public ControlChartViewModelTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));
        _api.Plans.AddRange([Oven, Rail]);
        _api.Charts[1] = RunChart(alerts: [new(9, 1, "3V3_RAIL", "WE4", 106, T0.AddMinutes(6), "Open", null)]);
        _api.Charts[2] = RunChart() with { PlanId = 2, PlanCode = "REFLOW_PEAK_TEMP" };
    }

    private async Task<ControlChartViewModel> LoadedAsync()
    {
        var vm = new ControlChartViewModel(_api, _rt, new ImmediateDispatcher(), _time, _pages);
        await vm.LoadAsync();
        await Task.Yield();
        return vm;
    }

    [Fact]
    public async Task Opens_on_the_3v3_rail_with_both_charts_capability_and_open_alerts()
    {
        var vm = await LoadedAsync();

        Assert.Equal(("3V3_RAIL", 100), (vm.SelectedPlan!.Code, vm.SelectedTake));
        Assert.Equal([(1, 100)], _api.ChartCalls);
        Assert.Equal(2, vm.IChart.Series.Count);
        Assert.Single(vm.MrChart.Series);
        Assert.Contains("3.53", vm.CapabilityText);
        Assert.Equal("PCB-A / TEST", vm.SourceText);
        var alert = Assert.Single(vm.Alerts);
        Assert.Equal(("08:06", "WE4"), (alert.Time[^5..], alert.Rule));
        Assert.StartsWith("Run", alert.Meaning);
    }

    [Fact]
    public async Task Another_plan_or_another_number_of_points_reloads()
    {
        var vm = await LoadedAsync();

        vm.SelectedTake = 25;
        vm.SelectedPlan = vm.Plans.Single(p => p.Code == "REFLOW_PEAK_TEMP");

        Assert.Equal([(1, 100), (1, 25), (2, 25)], _api.ChartCalls);
        Assert.Equal("L1-REFLOW-01/peakTemp", vm.SourceText);
    }

    // SignalR: a new alert on the plan on screen reloads it at once; on another plan it is only announced.
    [Fact]
    public async Task A_pushed_alert_reloads_its_own_plan_and_announces_the_others()
    {
        var vm = await LoadedAsync();

        _rt.Client.Push(new SpcAlertDto(10, 1, "3V3_RAIL", "WE1", 120, T0, "Open", null));
        _rt.Client.Push(new SpcAlertDto(11, 2, "REFLOW_PEAK_TEMP", "OutOfSpec", 121, T0, "Open", null));

        Assert.Equal([(1, 100), (1, 100)], _api.ChartCalls);
        Assert.Equal("New SPC alert OutOfSpec on REFLOW_PEAK_TEMP.", vm.NoticeText);
    }

    [Fact]
    public async Task New_points_are_polled_every_fifteen_seconds_until_the_screen_closes()
    {
        var vm = await LoadedAsync();

        _time.Advance(ControlChartViewModel.PollInterval);
        await Task.Delay(50);
        Assert.Equal(2, _api.ChartCalls.Count);

        await vm.CloseAsync();
        _time.Advance(ControlChartViewModel.PollInterval);
        _rt.Client.Push(new SpcAlertDto(10, 1, "3V3_RAIL", "WE1", 120, T0, "Open", null));
        await Task.Delay(50);
        Assert.Equal(2, _api.ChartCalls.Count);
    }

    // [M7 step 8] "Create NCR" on an alert opens the NCR screen with the form filled, the board of that point included.
    [Fact]
    public async Task Create_ncr_on_an_alert_opens_the_ncr_screen_filled_from_it()
    {
        var vm = await LoadedAsync();
        Assert.True(vm.Alerts[0].CanCreateNcr);

        await vm.CreateNcrCommand.ExecuteAsync(vm.Alerts[0]);

        Assert.Equal([typeof(NcrViewModel)], _pages.Shown);
        var tokens = new MiniMes.Client.Core.Auth.TokenStore();
        tokens.Set(new MiniMes.Contracts.Auth.LoginResponse("t", T0, "qc01", "QC One", ["QC"], null));
        var ncr = new NcrViewModel(new FakeNcrApi(), new DefectCodesApi(), tokens, new AnswerDialogs());
        await ((Func<NcrViewModel, Task>)_pages.LastInit!)(ncr);
        Assert.Equal(("SpcAlert", (long?)9, "PCBA6"), (ncr.NewSource, ncr.NewSpcAlertId, ncr.NewSerials));
        Assert.StartsWith("WE4 on 3V3_RAIL", ncr.NewDescription);
    }

    [Theory]
    [InlineData("WE1", "1 point beyond 3σ")]
    [InlineData("DataGap", "Data gap: no value from the machine")]
    [InlineData("XYZ", "XYZ")]
    public void Rule_codes_are_explained_in_words(string rule, string meaning)
        => Assert.Equal(meaning, ControlChartViewModel.Meaning(rule));
}
