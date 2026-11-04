using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Realtime;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Machines;
using MiniMes.Client.Core.ViewModels.Oee;
using MiniMes.Contracts.Quality;
using OxyPlot;

namespace MiniMes.Client.Core.ViewModels.Quality;

// One open alert in the list next to the chart.
public sealed record SpcAlertRow(SpcAlertDto Dto, string Time, string Rule, string Meaning);

// Spec M7 step 7: the control chart of one characteristic. The server sends points, limits, violations and window
// statistics (one rule engine, on the server); this class only arranges them into two PlotModels and a list.
//   - New SPC alert (SignalR SpcAlertRaised, QC / Supervisor / Admin): the chart of that plan reloads at once.
//   - New points without an alert arrive every 15 s by polling (a reflow sample every 30 s; nobody needs it faster).
public partial class ControlChartViewModel : ObservableObject, ILoadable, IClosable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    public const string DefaultPlan = "3V3_RAIL";

    private readonly IQualityApi _api;
    private readonly IRealtimeClient _realtime;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private bool _loaded;

    public ControlChartViewModel(IQualityApi api, IRealtimeClientFactory realtime, IUiDispatcher ui, TimeProvider time)
    {
        _api = api;
        _realtime = realtime.Create();
        _ui = ui;
        _time = time;
        _realtime.SpcAlertRaised += OnAlertRaised;
        _realtime.ConnectionChanged += connected => _ui.Post(() => IsConnected = connected);
        _realtime.Resync += RefreshAsync;
    }

    public ObservableCollection<InspectionPlanDto> Plans { get; } = [];
    public IReadOnlyList<int> Takes { get; } = [25, 50, 100, 200];
    public ObservableCollection<SpcAlertRow> Alerts { get; } = [];

    // Tooltip on hover (time, value, serial, rules), as on the M5 charts.
    public PlotController IChartController { get; } = Charts.HoverController();
    public PlotController MrChartController { get; } = Charts.HoverController();

    [ObservableProperty] private InspectionPlanDto? _selectedPlan;
    [ObservableProperty] private int _selectedTake = 100;
    [ObservableProperty] private PlotModel _iChart = new();
    [ObservableProperty] private PlotModel _mrChart = new();
    [ObservableProperty] private string _capabilityText = string.Empty;
    [ObservableProperty] private string _windowText = string.Empty;
    [ObservableProperty] private string _baselineText = string.Empty;
    [ObservableProperty] private string _sourceText = string.Empty;
    [ObservableProperty] private string _noticeText = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReconnecting))]
    private bool _isConnected = true;

    public bool IsReconnecting => !IsConnected;

    // The last chart shown: what the tests look at.
    public SpcChartDto? Chart { get; private set; }

    public async Task LoadAsync()
    {
        try
        {
            foreach (var plan in await _api.GetPlansAsync()) Plans.Add(plan);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        _loaded = true;
        SelectedPlan = Plans.FirstOrDefault(p => p.Code == DefaultPlan) ?? Plans.FirstOrDefault();   // refreshes
        _ = _realtime.StartAsync();
        _ = PollAsync(_stop.Token);
    }

    partial void OnSelectedPlanChanged(InspectionPlanDto? value)
    {
        NoticeText = string.Empty;
        if (_loaded) _ = RefreshAsync();
    }

    partial void OnSelectedTakeChanged(int value) { if (_loaded) _ = RefreshAsync(); }

    // SignalR thread: only the plan on screen reloads; an alert on another plan is announced, not loaded.
    private void OnAlertRaised(SpcAlertDto alert)
    {
        if (alert.PlanId == SelectedPlan?.Id) _ = RefreshAsync();
        else _ui.Post(() => NoticeText = string.Format(CultureInfo.CurrentCulture, Strings.Spc_AlertOnOtherPlan, alert.Rule, alert.PlanCode));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await RefreshAsync();
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (SelectedPlan is not { } plan) return;
        var take = SelectedTake;
        try
        {
            var chart = await _api.GetChartAsync(plan.Id, take);
            _ui.Post(() => Show(chart, take));
        }
        catch (ApiException ex)
        {
            _ui.Post(() => ErrorMessage = ex.Message);
        }
    }

    private void Show(SpcChartDto chart, int take)
    {
        if (chart.PlanId != SelectedPlan?.Id || take != SelectedTake) return;   // the user moved on meanwhile
        ErrorMessage = string.Empty;
        Chart = chart;
        // New models, assigned: the view redraws through the binding (as in M5), no InvalidatePlot from here.
        IChart = ControlChartBuilder.IChart(chart, ToPlant);
        MrChart = ControlChartBuilder.MrChart(chart);

        CapabilityText = chart.Window is { } w
            ? string.Format(CultureInfo.CurrentCulture, Strings.Spc_Capability, Num(w.Cp), Num(w.Cpk))
            : Strings.Spc_NotEnoughPoints;
        WindowText = chart.Window is { } s
            ? string.Format(CultureInfo.CurrentCulture, Strings.Spc_Window, s.Count, s.Mean, s.Sigma)
            : string.Empty;
        BaselineText = chart.Limits is { } l
            ? string.Format(CultureInfo.CurrentCulture, Strings.Spc_Baseline, l.Mean, l.Sigma, l.Ucl, l.Lcl, ToPlant(l.CalculatedAt), l.CalculatedBy)
            : Strings.Spc_NoBaseline;
        SourceText = SelectedPlan is { } p
            ? p.MachineTag ?? $"{p.ItemCode} / {p.OperationCode}"
            : string.Empty;

        Alerts.Clear();
        foreach (var a in chart.OpenAlerts)
            Alerts.Add(new SpcAlertRow(a, $"{ToPlant(a.RaisedAt):dd/MM HH:mm}", a.Rule, Meaning(a.Rule)));
    }

    public static string Meaning(string rule) => rule switch
    {
        "WE1" => Strings.Spc_RuleWe1,
        "WE2" => Strings.Spc_RuleWe2,
        "WE3" => Strings.Spc_RuleWe3,
        "WE4" => Strings.Spc_RuleWe4,
        "OutOfSpec" => Strings.Spc_RuleOutOfSpec,
        "DataGap" => Strings.Spc_RuleDataGap,
        _ => rule,
    };

    private DateTime ToPlant(DateTime utc) => AndonRowViewModel.ToLocal(utc, _time);

    private static string Num(double? v) => v is { } x ? x.ToString("0.00", CultureInfo.CurrentCulture) : "—";

    public async Task CloseAsync()
    {
        await _stop.CancelAsync();
        _realtime.SpcAlertRaised -= OnAlertRaised;
        _realtime.Resync -= RefreshAsync;
        await _realtime.DisposeAsync();
    }
}
