using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Realtime;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Machines;
using MiniMes.Contracts.Andon;

namespace MiniMes.Client.Core.ViewModels.Oee;

// One hour of the shift on the big screen: what the cycle time allows, what came out good, the gap.
public sealed record LineTvHour(string Hour, long Plan, long Actual)
{
    public long Gap => Actual - Plan;
    public bool IsBehind => Gap < 0;
    // An arrow as well as the colour: the screen must work for colour-blind readers (spec 2.8 trap 1).
    public string GapText => IsBehind ? $"▼ {Gap:N0}" : $"▲ +{Gap:N0}";
}

// [v2] Spec M5 2.8: the TV at the head of the line, read from 5-10 m. No buttons, no mouse, nobody logged in:
//   - logs in by itself with a badge (Operator role) and again when the token expires (401);
//   - the running shift's OEE, good vs plan, hours, open Andons (with their escalation level), machine states;
//   - REST every 15 s (numbers that jump every second are hard to read), Andon and state changes at once (SignalR);
//   - several lines: one after the other every 20 s;
//   - API or SignalR lost: an "Offline - data at hh:mm" band, so old numbers never look like new ones.
public partial class LineTvViewModel : ObservableObject, ILoadable, IClosable
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    private readonly IOeeApi _oee;
    private readonly IMachineApi _machines;
    private readonly IAuthClient _auth;
    private readonly IRealtimeClient _realtime;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly ClientOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<long, ulong> _seen = [];
    private bool _loggedIn;
    private DateTime? _lastData;
    private int _index;

    public LineTvViewModel(IOeeApi oee, IMachineApi machines, IAuthClient auth, IRealtimeClientFactory realtime,
        IUiDispatcher ui, IOptions<ClientOptions> options, TimeProvider time)
    {
        _oee = oee;
        _machines = machines;
        _auth = auth;
        _realtime = realtime.Create();
        _ui = ui;
        _time = time;
        _options = options.Value;
        Lines = _options.LineTvLines.Count > 0 ? _options.LineTvLines : [LineDashboardViewModel.DefaultLine];
        _currentLine = Lines[0];

        _realtime.AndonChanged += d => _ui.Post(() => { if (d.Line == CurrentLine) ApplyAndon(d); });
        _realtime.MachineStateChanged += d => _ui.Post(() =>
        {
            if (d.Line == CurrentLine) Tiles.FirstOrDefault(t => t.Code == d.Machine)?.Apply(d);
        });
        _realtime.ConnectionChanged += connected => _ui.Post(() => { if (!connected) GoOffline(); });
        _realtime.Resync += RefreshAsync;
    }

    public IReadOnlyList<string> Lines { get; }
    public ObservableCollection<LineTvHour> Hours { get; } = [];
    public ObservableCollection<AndonRowViewModel> Andons { get; } = [];
    public ObservableCollection<MachineTileViewModel> Tiles { get; } = [];

    [ObservableProperty] private string _currentLine;
    [ObservableProperty] private string _oeeText = "—";
    [ObservableProperty] private string _goodText = "—";
    [ObservableProperty] private string _planText = string.Empty;
    [ObservableProperty] private string _shiftText = string.Empty;
    [ObservableProperty] private bool _isBehind;
    [ObservableProperty] private int _andonCount;
    [ObservableProperty] private bool _isOffline;
    [ObservableProperty] private string _offlineText = string.Empty;

    public async Task LoadAsync()
    {
        foreach (var line in Lines) await _realtime.JoinLineAsync(line);   // all of them: no rejoin when rotating
        _ = _realtime.StartAsync();
        await RefreshAsync();
        _ = LoopAsync(RefreshInterval, RefreshAsync, _stop.Token);
        if (Lines.Count > 1 && _options.LineTvRotateSeconds > 0)
            _ = LoopAsync(TimeSpan.FromSeconds(_options.LineTvRotateSeconds), NextLineAsync, _stop.Token);
    }

    private async Task LoopAsync(TimeSpan period, Func<Task> action, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(period, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) await action();
        }
        catch (OperationCanceledException) { }
    }

    private Task NextLineAsync()
    {
        _index = (_index + 1) % Lines.Count;
        _ui.Post(() => CurrentLine = Lines[_index]);
        return RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var line = Lines[_index];
        try
        {
            if (!_loggedIn)
            {
                await _auth.LoginOperatorAsync(_options.LineTvBadge, _options.StationCode ?? string.Empty);
                _loggedIn = true;
            }
            var shift = await _oee.GetShiftAsync(null, null, null, line);
            var status = await _machines.GetLineStatusAsync(line);
            var andons = await _machines.GetOpenAndonsAsync(line);
            _ui.Post(() =>
            {
                if (line != CurrentLine) return;   // rotated meanwhile
                Show(shift);
                Tiles.Clear();
                foreach (var m in status.Machines) Tiles.Add(new MachineTileViewModel(m));
                Andons.Clear();
                foreach (var a in andons) ApplyAndon(a);
                _lastData = _time.GetUtcNow().UtcDateTime;
                IsOffline = false;
            });
        }
        catch (ApiException ex)
        {
            if (ex.Status == HttpStatusCode.Unauthorized) _loggedIn = false;   // token expired: log in on the next tick
            _ui.Post(GoOffline);
        }
    }

    private void Show(Contracts.Oee.ShiftOeeDto shift)
    {
        ShiftText = shift.Shift is { } s ? string.Format(Strings.LineTv_Shift, CurrentLine, s) : string.Format(Strings.LineTv_NoShift, CurrentLine);
        var total = shift.Total;
        OeeText = total?.Oee is { } oee ? oee.ToString("0%", System.Globalization.CultureInfo.InvariantCulture) : "—";
        // Plan so far = planned time up to now / the shift's cycle time (Ideal / Total): the same target as the hours.
        double? cycle = total is { TotalCount: > 0 } ? total.IdealSeconds / total.TotalCount : null;
        long? plan = cycle is { } c && c > 0 ? (long)(total!.PlannedSeconds / c) : null;
        GoodText = total is null ? "—" : total.GoodCount.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        PlanText = plan is { } p ? string.Format(Strings.LineTv_Plan, p) : string.Empty;
        IsBehind = plan is { } pl && total!.GoodCount < pl;
        Hours.Clear();
        foreach (var h in shift.Hours)
            Hours.Add(new LineTvHour($"{h.Hour:00}:00", (long)HourlyOutputViewModel.Target(h.Figures, cycle), h.Figures.GoodCount));
    }

    // Raise, acknowledge, resolve: on the screen at once, never back to an older version of a call.
    private void ApplyAndon(AndonDto dto)
    {
        var version = AndonVersions.Of(dto);
        if (_seen.TryGetValue(dto.Id, out var seen) && version < seen) return;
        _seen[dto.Id] = version;
        var row = Andons.FirstOrDefault(r => r.Id == dto.Id);
        if (!AndonVersions.IsOpen(dto)) { if (row is not null) Andons.Remove(row); }
        else if (row is null) Andons.Add(new AndonRowViewModel(dto, _time));
        else row.TryApply(dto);
        AndonCount = Andons.Count;
    }

    private void GoOffline()
    {
        IsOffline = true;
        OfflineText = string.Format(Strings.LineTv_Offline,
            _lastData is { } at ? AndonRowViewModel.ToLocal(at, _time).ToString("HH:mm") : "—");
    }

    public async Task CloseAsync()
    {
        await _stop.CancelAsync();
        _realtime.Resync -= RefreshAsync;
        await _realtime.DisposeAsync();
    }
}
