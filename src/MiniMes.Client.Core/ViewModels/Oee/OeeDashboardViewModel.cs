using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Realtime;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Oee;
using MiniMes.Client.Core.ViewModels.Machines;

namespace MiniMes.Client.Core.ViewModels.Oee;

// A machine to look at, or null = the whole line (the server adds the machines up).
public sealed record MachineChoice(string? Code, string Label);

// A stop still to explain (spec M5 step 6), with the reason the supervisor is choosing.
public partial class UnclassifiedRowViewModel(UnclassifiedDowntimeDto dto, TimeProvider time) : ObservableObject
{
    public UnclassifiedDowntimeDto Dto { get; } = dto;
    public long Id => Dto.Id;
    public string Machine => Dto.Machine;
    public string State => Dto.State;
    public string Period => $"{AndonRowViewModel.ToLocal(Dto.StartAt, time):HH:mm}–{AndonRowViewModel.ToLocal(Dto.EndAt, time):HH:mm}";
    public double Minutes => Dto.Minutes;

    [ObservableProperty]
    private DowntimeReasonDto? _selectedReason;

    [ObservableProperty]
    private string _note = string.Empty;
}

// Spec M5 step 8: the OEE dashboard of a line or a machine, for the running shift or a past one.
//   - A/P/Q/OEE cards + warning badges, Pareto top 5, good boards per hour against the target, stops to classify.
//   - Every number comes from the API (one OEE formula, on the server); this class only arranges it.
//   - The running shift is polled every 15 s (OEE does not change by the second); SignalR makes it refresh at once
//     when a stop needs a reason or the job closes an hour.
public partial class OeeDashboardViewModel : ObservableObject, ILoadable, IClosable
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly string[] Classifiers = ["Supervisor", "Admin"];

    private readonly IOeeApi _oee;
    private readonly IMachineApi _machines;
    private readonly IMasterDataApi _master;
    private readonly IRealtimeClient _realtime;
    private readonly IUiDispatcher _ui;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private string? _line;
    private DateOnly? _shownDate;   // the production date on screen: what Export writes
    private bool _loaded;

    public OeeDashboardViewModel(IOeeApi oee, IMachineApi machines, IMasterDataApi master, IRealtimeClientFactory realtime,
        IUiDispatcher ui, ITokenStore tokens, IDialogService dialogs, TimeProvider time)
    {
        _dialogs = dialogs;
        _oee = oee;
        _machines = machines;
        _master = master;
        _realtime = realtime.Create();
        _ui = ui;
        _time = time;
        CanClassify = tokens.Session?.Roles.Any(Classifiers.Contains) == true;
        _selectedDate = AndonRowViewModel.ToLocal(time.GetUtcNow().UtcDateTime, time).Date;

        _realtime.DowntimeNeedsClassification += d => _ui.Post(() => AddUnclassified(d));
        _realtime.OeeSnapshotClosed += d => { if (d.Line == _line) _ = RefreshAsync(); };
        _realtime.ConnectionChanged += connected => _ui.Post(() => IsConnected = connected);
        _realtime.Resync += RefreshAsync;
    }

    public ObservableCollection<LineDto> Lines { get; } = [];
    public ObservableCollection<MachineChoice> Machines { get; } = [];
    public ObservableCollection<string> Shifts { get; } = [];
    public ObservableCollection<DowntimeReasonDto> DowntimeReasons { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];
    public ObservableCollection<UnclassifiedRowViewModel> Unclassified { get; } = [];
    public ParetoViewModel Pareto { get; } = new();
    public HourlyOutputViewModel Hourly { get; } = new();

    // The API decides (403); the screen only hides the Save button from the others.
    public bool CanClassify { get; }

    [ObservableProperty] private LineDto? _selectedLine;
    [ObservableProperty] private MachineChoice? _selectedMachine;
    [ObservableProperty] private bool _isCurrentShift = true;
    [ObservableProperty] private DateTime _selectedDate;
    [ObservableProperty] private string? _selectedShift;
    [ObservableProperty] private bool _paretoByCount;

    [ObservableProperty] private OeeCardViewModel _availability = new(Strings.Oee_Availability);
    [ObservableProperty] private OeeCardViewModel _performance = new(Strings.Oee_Performance);
    [ObservableProperty] private OeeCardViewModel _quality = new(Strings.Oee_Quality);
    [ObservableProperty] private OeeCardViewModel _oeeCard = new(Strings.Oee_Oee);

    [ObservableProperty] private string _shiftText = string.Empty;
    [ObservableProperty] private string _countsText = string.Empty;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _exportMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReconnecting))]
    private bool _isConnected = true;

    public bool IsReconnecting => !IsConnected;

    public async Task LoadAsync()
    {
        try
        {
            foreach (var line in (await _machines.GetLinesAsync()).Where(l => l.IsActive)) Lines.Add(line);
            foreach (var reason in await _machines.GetDowntimeReasonsAsync()) DowntimeReasons.Add(reason);
            foreach (var shift in (await _master.GetShiftsAsync()).Where(s => s.IsActive).OrderBy(s => s.StartTime)) Shifts.Add(shift.Code);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        SelectedShift = Shifts.FirstOrDefault();
        _loaded = true;
        SelectedLine = Lines.FirstOrDefault(l => l.Code == LineDashboardViewModel.DefaultLine) ?? Lines.FirstOrDefault();
        _ = _realtime.StartAsync();
        _ = PollAsync(_stop.Token);
    }

    partial void OnSelectedLineChanged(LineDto? value) => _ = SwitchLineAsync(value?.Code);
    partial void OnSelectedMachineChanged(MachineChoice? value) => Changed();
    partial void OnIsCurrentShiftChanged(bool value) => Changed();
    partial void OnSelectedDateChanged(DateTime value) { if (!IsCurrentShift) Changed(); }
    partial void OnSelectedShiftChanged(string? value) { if (!IsCurrentShift) Changed(); }
    partial void OnParetoByCountChanged(bool value) => Changed();

    private void Changed() { if (_loaded) _ = RefreshAsync(); }

    private async Task SwitchLineAsync(string? code)
    {
        if (code is null || code == _line) return;
        if (_line is { } previous) await _realtime.LeaveLineAsync(previous);
        _line = code;
        Machines.Clear();
        Machines.Add(new MachineChoice(null, Strings.Oee_AllMachines));
        SelectedMachine = Machines[0];   // refreshes
        await _realtime.JoinLineAsync(code);
    }

    // Only the running shift moves; a past one is fixed (a reason change arrives as OeeSnapshotClosed).
    private async Task PollAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                if (IsCurrentShift) await RefreshAsync();
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_line is not { } line) return;
        var machine = SelectedMachine?.Code;
        var lineScope = machine is null ? line : null;
        DateOnly? date = IsCurrentShift ? null : DateOnly.FromDateTime(SelectedDate);
        var shiftCode = IsCurrentShift ? null : SelectedShift;
        if (!IsCurrentShift && shiftCode is null) return;
        try
        {
            var shift = await _oee.GetShiftAsync(date, shiftCode, machine, lineScope);
            ParetoDto? pareto = null;
            IReadOnlyList<UnclassifiedDowntimeDto> stops = [];
            if (shift is { ShiftDate: { } d, Shift: { } s, FromUtc: { } from, ToUtc: { } to })
            {
                pareto = await _oee.GetParetoAsync(d, s, machine, lineScope, ParetoByCount ? ParetoBy.Count : ParetoBy.Minutes);
                stops = await _oee.GetUnclassifiedAsync(from, to);
            }
            _ui.Post(() => Show(line, machine, shift, pareto, stops));
        }
        catch (ApiException ex)
        {
            _ui.Post(() => ErrorMessage = ex.Message);
        }
    }

    private void Show(string line, string? machine, ShiftOeeDto shift, ParetoDto? pareto, IReadOnlyList<UnclassifiedDowntimeDto> stops)
    {
        if (line != _line || machine != SelectedMachine?.Code) return;   // the user moved on meanwhile
        ErrorMessage = string.Empty;
        _shownDate = shift.ShiftDate;
        var f = shift.Total;
        Availability = Availability.With(f?.Availability);
        Performance = Performance.With(f?.Performance);
        Quality = Quality.With(f?.Quality);
        OeeCard = OeeCard.With(f?.Oee);
        Warnings.Clear();
        foreach (var w in f?.Warnings ?? []) Warnings.Add(WarningText(w));
        ShiftText = shift is { Shift: { } s, ShiftDate: { } d, FromUtc: { } from, ToUtc: { } to }
            ? string.Format(Strings.Oee_ShiftText, s, d.ToString("yyyy-MM-dd"), $"{AndonRowViewModel.ToLocal(from, _time):HH:mm}",
                $"{AndonRowViewModel.ToLocal(to, _time):HH:mm}", shift.IsRunning ? Strings.Oee_Running : string.Empty)
            : Strings.Oee_NoShift;
        CountsText = f is null ? string.Empty : string.Format(Strings.Oee_Counts, f.TotalCount, f.GoodCount, f.ScanCount,
            f.PlannedSeconds / 60.0, f.RunSeconds / 60.0);

        // The machine list of the line comes with the line's data; keep the user's choice.
        if (machine is null)
            foreach (var m in shift.Machines.Where(m => Machines.All(c => c.Code != m.Machine)))
                Machines.Add(new MachineChoice(m.Machine, m.Machine));

        Hourly.Load(shift.Hours);
        Pareto.Load(pareto);
        Unclassified.Clear();
        foreach (var stop in stops) AddUnclassified(stop);
    }

    private void AddUnclassified(UnclassifiedDowntimeDto dto)
    {
        if (dto.Line != _line || (SelectedMachine?.Code is { } m && dto.Machine != m)) return;
        if (Unclassified.Any(r => r.Id == dto.Id)) return;   // a push may come twice (retried batch)
        Unclassified.Insert(0, new UnclassifiedRowViewModel(dto, _time));
    }

    [RelayCommand]
    private async Task ClassifyAsync(UnclassifiedRowViewModel row)
    {
        if (row.SelectedReason is not { } reason)
        {
            ErrorMessage = Strings.Dashboard_ChooseReason;
            return;
        }
        ErrorMessage = string.Empty;
        try
        {
            await _oee.AssignReasonAsync(row.Id, reason.Id, string.IsNullOrWhiteSpace(row.Note) ? null : row.Note);
            Unclassified.Remove(row);
            await RefreshAsync();   // the running shift is recomputed at once; a past one when the job has run
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;   // OTHER without a note, stop still going on...
        }
    }

    // [v2] Step 8b: the Excel files of the day on screen and its line (spec 2.7). The server builds them from the same
    // queries as this screen, so the numbers match.
    [RelayCommand]
    private Task ExportOeeAsync() => ExportAsync("oee", (d, line) => _oee.GetOeeExcelAsync(d, d, line));

    [RelayCommand]
    private Task ExportParetoAsync()
        => ExportAsync("downtime-pareto", (d, line) => _oee.GetParetoExcelAsync(d, d, line, ParetoByCount ? ParetoBy.Count : ParetoBy.Minutes));

    private async Task ExportAsync(string name, Func<DateOnly, string?, Task<byte[]>> download)
    {
        var day = _shownDate ?? DateOnly.FromDateTime(SelectedDate);
        if (_dialogs.PickFileToSave($"{name}_{_line}_{day:yyyyMMdd}.xlsx", ExcelFilter) is not { } path) return;
        try
        {
            await File.WriteAllBytesAsync(path, await download(day, _line));
            ExportMessage = string.Format(Strings.Oee_Exported, path);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private const string ExcelFilter = "Excel workbook (*.xlsx)|*.xlsx";

    public static string WarningText(string warning) => warning switch
    {
        nameof(OeeWarningNames.PerformanceOver100) => Strings.Oee_WarningPerformanceOver100,
        nameof(OeeWarningNames.GoodExceedsTotal) => Strings.Oee_WarningGoodExceedsTotal,
        nameof(OeeWarningNames.DowntimeExceedsPlanned) => Strings.Oee_WarningDowntimeExceedsPlanned,
        nameof(OeeWarningNames.CounterMismatch) => Strings.Oee_WarningCounterMismatch,
        _ => warning,
    };

    // The flag names the API sends (Domain OeeWarning, which the client cannot see).
    private enum OeeWarningNames { PerformanceOver100, GoodExceedsTotal, DowntimeExceedsPlanned, CounterMismatch }

    public async Task CloseAsync()
    {
        await _stop.CancelAsync();
        _realtime.Resync -= RefreshAsync;
        await _realtime.DisposeAsync();
    }
}
