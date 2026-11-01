using System.Net;
using Microsoft.Extensions.Time.Testing;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Oee;
using MiniMes.Contracts.Auth;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Oee;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;
using static MiniMes.UnitTests.Client.FakeMachineApi;

namespace MiniMes.UnitTests.Client;

internal sealed class FakeOeeApi : IOeeApi
{
    public List<(DateOnly? Date, string? Shift, string? Machine, string? Line)> ShiftCalls { get; } = [];
    public List<(DateOnly Date, string Shift, string? Machine, string? Line, string By)> ParetoCalls { get; } = [];
    public List<(long Id, int ReasonId, string? Note)> Assigned { get; } = [];
    public Func<ShiftOeeDto> Shift { get; set; } = () => OeeDashboardViewModelTests.SampleShift();
    public ParetoDto Pareto { get; set; } = OeeDashboardViewModelTests.SamplePareto();
    public List<UnclassifiedDowntimeDto> Stops { get; } = [];
    public Exception? AssignFails { get; set; }

    public Task<ShiftOeeDto> GetShiftAsync(DateOnly? date, string? shift, string? machine, string? line, CancellationToken ct = default)
    {
        ShiftCalls.Add((date, shift, machine, line));
        return Task.FromResult(Shift());
    }

    public Task<ParetoDto> GetParetoAsync(DateOnly date, string shift, string? machine, string? line, string by, CancellationToken ct = default)
    {
        ParetoCalls.Add((date, shift, machine, line, by));
        return Task.FromResult(Pareto);
    }

    public Task<IReadOnlyList<UnclassifiedDowntimeDto>> GetUnclassifiedAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<UnclassifiedDowntimeDto>>(Stops.ToList());

    public Task<DowntimeSegmentDto> AssignReasonAsync(long segmentId, int downtimeReasonId, string? note, CancellationToken ct = default)
    {
        if (AssignFails is { } ex) return Task.FromException<DowntimeSegmentDto>(ex);
        Assigned.Add((segmentId, downtimeReasonId, note));
        Stops.RemoveAll(s => s.Id == segmentId);
        return Task.FromResult(new DowntimeSegmentDto(segmentId, 1, "L1-SMT-01", "Down", T0, T0, downtimeReasonId, "X", note));
    }
}

internal sealed class ShiftsMasterDataApi : FakeMasterDataApi
{
    public override Task<IReadOnlyList<ShiftDto>> GetShiftsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ShiftDto>>(
        [
            new(2, "S2", "Afternoon", new(14, 0), new(22, 0), ["Mon"], false, 480, 420, [], true),
            new(1, "S1", "Morning", new(6, 0), new(14, 0), ["Mon"], false, 480, 420, [], true),
            new(9, "SX", "Old", new(6, 0), new(7, 0), ["Mon"], false, 60, 60, [], false),
        ]);
}

// Spec M5 step 8: the dashboard without a server. Numbers come computed from the (fake) API; the tests check what
// the screen does with them: cards, badges, charts, filters, polling, pushes, classifying.
public class OeeDashboardViewModelTests
{
    private static readonly DateOnly Day = new(2026, 10, 27);
    private readonly FakeOeeApi _oee = new();
    private readonly FakeMachineApi _machines = new();
    private readonly FakeRealtimeFactory _rt = new();
    private readonly TokenStore _tokens = new();
    private readonly FakeTimeProvider _time = new(T0);   // 09:00 plant time, during S1

    public OeeDashboardViewModelTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh"));   // the PC on the shop floor
        Login("Supervisor");
    }

    private void Login(string role) => _tokens.Set(new LoginResponse("t", T0, "u1", "User", [role], null));

    private static OeeFiguresDto Figures(int planned, int run, long total, long good, double ideal, double? a, double? p,
        double? q, double? oee, params string[] warnings) => new(planned, run, total, good, total, ideal, a, p, q, oee, warnings);

    // The sample shift of 1.2.2 with three closed hours: 60 planned min at 20 s, a 15-min break hour, a lunch hour.
    public static ShiftOeeDto SampleShift() => new(Day, "S1", T0.AddHours(-3), T0, true,
        Figures(405 * 60, 360 * 60, 1000, 970, 20_000, 0.8889, 0.9259, 0.97, 0.7984),
        [new(1, "L1-SMT-01", Figures(405 * 60, 360 * 60, 1000, 970, 20_000, 0.8889, 0.9259, 0.97, 0.7984)),
         new(2, "L1-REFLOW-01", Figures(405 * 60, 405 * 60, 1000, 970, 20_000, 1, 0.82, 0.97, 0.80))],
        [new(6, T0.AddHours(-3), T0.AddHours(-2), Figures(3600, 3600, 170, 168, 3400, 1, 0.94, 0.99, 0.93)),
         new(8, T0.AddHours(-1), T0, Figures(2700, 2700, 120, 118, 2400, 1, 0.89, 0.98, 0.87)),
         new(11, T0, T0.AddHours(1), Figures(0, 0, 0, 0, 0, null, null, null, null))]);

    public static ParetoDto SamplePareto() => new(Day, Day, ParetoBy.Minutes, 100, 12,
    [
        new("FDR-JAM", "Feeder jam", true, 40, 3, 40, 40), new("MAT-WAIT", "Material", true, 25, 2, 25, 65),
        new("CHG-MODEL", "Changeover", true, 15, 1, 15, 80), new("QC-WAIT", "QC", true, 10, 2, 10, 90),
        new("(Down)", "Down", true, 6, 3, 6, 96), new("OTHER", "Other", true, 4, 1, 4, 100),
    ]);

    private OeeDashboardViewModel Vm() => new(_oee, _machines, new ShiftsMasterDataApi(), _rt, new ImmediateDispatcher(), _tokens, _time);

    private async Task<OeeDashboardViewModel> LoadedAsync()
    {
        var vm = Vm();
        await vm.LoadAsync();
        await vm.RefreshAsync();   // the line switch starts a refresh without waiting for it: be sure it is done
        return vm;
    }

    [Fact]
    public async Task Current_shift_of_the_line_fills_cards_and_text()
    {
        var vm = await LoadedAsync();

        Assert.Equal((null, null, null, "L1"), _oee.ShiftCalls[^1]);
        Assert.Equal(("88.9 %", "92.6 %", "97.0 %", "79.8 %"), (vm.Availability.Text, vm.Performance.Text, vm.Quality.Text, vm.OeeCard.Text));
        Assert.Contains("S1", vm.ShiftText);
        Assert.Equal(["S1", "S2"], vm.Shifts);                                    // active ones, in start order
        Assert.Equal([null, "L1-SMT-01", "L1-REFLOW-01"], vm.Machines.Select(m => m.Code));
        Assert.Equal(["L1"], _rt.Client.Joined);
    }

    [Fact]
    public async Task Undefined_ratio_shows_a_dash_and_warnings_become_badges()
    {
        _oee.Shift = () => SampleShift() with { Total = Figures(3600, 3600, 0, 0, 0, 1, 1.33, null, 0, "PerformanceOver100", "CounterMismatch") };

        var vm = await LoadedAsync();

        Assert.Equal(("—", "133.0 %", 100.0), (vm.Quality.Text, vm.Performance.Text, vm.Performance.Bar));   // bar full, text raw
        Assert.Equal([Strings.Oee_WarningPerformanceOver100, Strings.Oee_WarningCounterMismatch], vm.Warnings);
    }

    [Fact]
    public async Task Outside_every_shift_says_so()
    {
        _oee.Shift = () => new ShiftOeeDto(null, null, null, null, false, null, [], []);

        var vm = await LoadedAsync();

        Assert.Equal((Strings.Oee_NoShift, "—"), (vm.ShiftText, vm.OeeCard.Text));
        Assert.Empty(_oee.ParetoCalls);   // nothing to ask without a shift
    }

    [Fact]
    public async Task Machine_and_past_shift_filters_go_to_the_api()
    {
        var vm = await LoadedAsync();

        vm.SelectedMachine = vm.Machines.Single(m => m.Code == "L1-REFLOW-01");
        await vm.RefreshAsync();
        Assert.Equal((null, null, "L1-REFLOW-01", null), _oee.ShiftCalls[^1]);   // a machine: no line

        vm.IsCurrentShift = false;
        vm.SelectedDate = new DateTime(2026, 10, 20);
        vm.SelectedShift = "S2";
        await vm.RefreshAsync();
        Assert.Equal((new DateOnly(2026, 10, 20), "S2", "L1-REFLOW-01", null), _oee.ShiftCalls[^1]);
        Assert.Equal((Day, "S1", "L1-REFLOW-01", null, ParetoBy.Minutes), _oee.ParetoCalls[^1]);   // the shift the API answered

        vm.ParetoByCount = true;
        await vm.RefreshAsync();
        Assert.Equal(ParetoBy.Count, _oee.ParetoCalls[^1].By);
    }

    // Spec 2.6: poll the running shift every 10-15 s; a past shift does not move.
    [Fact]
    public async Task Running_shift_is_polled_every_15_seconds_a_past_one_is_not()
    {
        var vm = await LoadedAsync();
        var calls = _oee.ShiftCalls.Count;

        _time.Advance(OeeDashboardViewModel.PollInterval);
        await WaitUntilAsync(() => _oee.ShiftCalls.Count > calls);

        vm.IsCurrentShift = false;
        await vm.RefreshAsync();
        calls = _oee.ShiftCalls.Count;
        _time.Advance(OeeDashboardViewModel.PollInterval);
        await Task.Delay(200);
        Assert.Equal(calls, _oee.ShiftCalls.Count);
        await vm.CloseAsync();
        Assert.True(_rt.Client.Disposed);
    }

    [Fact]
    public async Task Pushes_add_a_stop_to_classify_and_a_closed_hour_refreshes()
    {
        var vm = await LoadedAsync();
        var stop = new UnclassifiedDowntimeDto(77, 1, "L1-SMT-01", "L1", "Down", T0.AddMinutes(-20), T0.AddMinutes(-12), 8);

        _rt.Client.Push(stop);
        _rt.Client.Push(stop);                                   // a retried batch: shown once
        _rt.Client.Push(stop with { Id = 78, Line = "L2" });    // another line
        Assert.Equal([77L], vm.Unclassified.Select(r => r.Id));
        Assert.Equal("08:40–08:48", vm.Unclassified[0].Period);   // plant time (UTC+7)

        var calls = _oee.ShiftCalls.Count;
        _rt.Client.Push(new OeeSnapshotClosedDto(1, "L1-SMT-01", "L1", Day, "S1", [8]));
        await WaitUntilAsync(() => _oee.ShiftCalls.Count > calls);
    }

    [Fact]
    public async Task Supervisor_classifies_a_stop_with_a_reason()
    {
        _oee.Stops.Add(new UnclassifiedDowntimeDto(77, 1, "L1-SMT-01", "L1", "Down", T0.AddMinutes(-20), T0.AddMinutes(-12), 8));
        var vm = await LoadedAsync();
        var row = Assert.Single(vm.Unclassified);
        Assert.True(vm.CanClassify);

        await vm.ClassifyCommand.ExecuteAsync(row);
        Assert.Equal(Strings.Dashboard_ChooseReason, vm.ErrorMessage);

        row.SelectedReason = Fault;
        row.Note = "nozzle";
        _oee.AssignFails = new ApiException(HttpStatusCode.BadRequest, "The reason OTHER needs a note.");
        await vm.ClassifyCommand.ExecuteAsync(row);
        Assert.Equal("The reason OTHER needs a note.", vm.ErrorMessage);

        _oee.AssignFails = null;
        await vm.ClassifyCommand.ExecuteAsync(row);
        Assert.Equal([(77L, Fault.Id, "nozzle")], _oee.Assigned);
        Assert.Empty(vm.Unclassified);
        Assert.Equal(string.Empty, vm.ErrorMessage);
    }

    [Fact]
    public async Task Operator_cannot_classify()
    {
        Login("Operator");

        Assert.False((await LoadedAsync()).CanClassify);
    }

    // The Pareto chart: top 5, columns (a transposed BarSeries in OxyPlot 2.2), cumulative % on a 0-100 axis.
    [Fact]
    public async Task Pareto_chart_has_top_5_columns_and_a_cumulative_line()
    {
        var vm = await LoadedAsync();
        var model = vm.Pareto.PlotModel;
        ((IPlotModel)model).Update(true);

        var axis = model.Axes.OfType<CategoryAxis>().Single();
        Assert.Equal(["FDR-JAM", "MAT-WAIT", "CHG-MODEL", "QC-WAIT", "(Down)"], axis.Labels);
        Assert.Equal(AxisPosition.Bottom, axis.Position);
        var bars = model.Series.OfType<BarSeries>().Single();
        Assert.True(bars.IsTransposed());                                         // vertical columns
        Assert.Equal([40.0, 25, 15, 10, 6], bars.Items.Select(i => i.Value));
        var line = model.Series.OfType<LineSeries>().Single();
        Assert.Equal([40.0, 65, 80, 90, 96], line.Points.Select(p => p.Y));
        var pct = model.Axes.Single(a => a.Key == "pct");
        Assert.Equal((0.0, 100.0, AxisPosition.Right), (pct.Minimum, pct.Maximum, pct.Position));
    }

    // Spec 2.6: target = planned seconds of the hour / ideal cycle time: 60 min at 20 s = 180, 45 min = 135, lunch = 0.
    [Fact]
    public async Task Hourly_chart_compares_good_boards_with_the_cycle_time_target()
    {
        var vm = await LoadedAsync();

        Assert.Equal([("06", 168L, 180.0), ("08", 118L, 135.0), ("11", 0L, 0.0)], vm.Hourly.Points);
        var model = vm.Hourly.PlotModel;
        ((IPlotModel)model).Update(true);
        Assert.True(model.Series.OfType<BarSeries>().Single().IsTransposed());
    }

    [Fact]
    public void Hour_without_counts_uses_the_shift_cycle_time()
        => Assert.Equal(180.0, HourlyOutputViewModel.Target(Figures(3600, 3600, 0, 0, 0, 1, 0, null, 0), shiftCycleSec: 20));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(condition());
    }
}
