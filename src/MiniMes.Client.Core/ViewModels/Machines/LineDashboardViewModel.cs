using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Realtime;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.MasterData;
using MiniMes.Contracts.Realtime;

namespace MiniMes.Client.Core.ViewModels.Machines;

// Spec M4 step 8 (2.6): one line's machines and open Andon calls, live. The pattern:
//   1. listen first (join the line group), 2. then load the REST snapshot, 3. apply pushes on top of it,
//   4. after every reconnect: join again and reload the snapshot (SignalR does not replay what was missed).
// Every push arrives on a background thread and is applied through IUiDispatcher.
public partial class LineDashboardViewModel : ObservableObject, ILoadable, IClosable
{
    public const string DefaultLine = "L1";
    private static readonly string[] Responders = ["Supervisor", "Maintenance", "Admin"];

    private readonly IMachineApi _api;
    private readonly IRealtimeClient _realtime;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    // Newest version seen per call, also for calls already removed: a late "Raised" must not bring a resolved call back.
    private readonly Dictionary<long, ulong> _seen = [];
    private string? _line;
    private Task _switching = Task.CompletedTask;

    public LineDashboardViewModel(IMachineApi api, IRealtimeClientFactory realtime, IUiDispatcher ui, ITokenStore tokens, TimeProvider time)
    {
        _api = api;
        _realtime = realtime.Create();
        _ui = ui;
        _time = time;
        CanRespond = tokens.Session?.Roles.Any(Responders.Contains) == true;

        _realtime.MachineStateChanged += d => _ui.Post(() => TileFor(d.Line, d.Machine)?.Apply(d));
        _realtime.MachineCounterUpdated += d => _ui.Post(() => TileFor(d.Line, d.Machine)?.Apply(d));
        _realtime.MachineProcessUpdated += d => _ui.Post(() => TileFor(d.Line, d.Machine)?.Apply(d));
        _realtime.AndonChanged += d => _ui.Post(() => { if (d.Line == _line) ApplyAndon(d); });
        _realtime.AndonEscalated += d => _ui.Post(() => EscalationAlert = string.Format(Strings.Dashboard_Escalated,
            d.Machine, d.Type, d.EscalationLevel, AndonRowViewModel.ToLocal(d.RaisedAt, _time).ToString("HH:mm")));
        _realtime.ConnectionChanged += connected => _ui.Post(() => IsConnected = connected);
        _realtime.Resync += ResyncAsync;
    }

    public ObservableCollection<LineDto> Lines { get; } = [];
    public ObservableCollection<MachineTileViewModel> Tiles { get; } = [];
    public ObservableCollection<AndonRowViewModel> Andons { get; } = [];
    public ObservableCollection<DowntimeReasonDto> DowntimeReasons { get; } = [];

    // Acknowledge / Resolve are shown to the roles the API lets do them (the API still checks: 403).
    public bool CanRespond { get; }

    [ObservableProperty]
    private LineDto? _selectedLine;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReconnecting))]
    private bool _isConnected = true;

    public bool IsReconnecting => !IsConnected;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // [v2] Shown to the roles an escalation is sent to (only they receive it).
    [ObservableProperty]
    private string _escalationAlert = string.Empty;

    public async Task LoadAsync()
    {
        try
        {
            foreach (var line in (await _api.GetLinesAsync()).Where(l => l.IsActive)) Lines.Add(line);
            foreach (var reason in await _api.GetDowntimeReasonsAsync()) DowntimeReasons.Add(reason);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        SelectedLine = Lines.FirstOrDefault(l => l.Code == DefaultLine) ?? Lines.FirstOrDefault();
        await _switching;
        // Snapshot already on screen (useful even if the hub is unreachable). Once connected, the client joins the
        // line and raises Resync, which reloads it: whatever changed before the connection is covered.
        // Never throws; when the API is down it retries in the background behind the "Reconnecting" banner.
        _ = _realtime.StartAsync();
    }

    partial void OnSelectedLineChanged(LineDto? value) => _switching = SwitchLineAsync(value?.Code);

    private async Task SwitchLineAsync(string? code)
    {
        if (code is null || code == _line) return;
        var previous = _line;
        _line = code;
        if (previous is not null) await _realtime.LeaveLineAsync(previous);
        await _realtime.JoinLineAsync(code);   // listen first...
        await LoadSnapshotAsync();             // ...then load: a change made in between arrives as a push
    }

    private Task ResyncAsync() => LoadSnapshotAsync();

    // Fetch on the calling thread (Resync: a background one), apply on the UI thread.
    // keepError: after a refused Acknowledge/Resolve the reload must not wipe the reason off the screen.
    private async Task LoadSnapshotAsync(bool keepError = false)
    {
        if (_line is not { } line) return;
        try
        {
            var status = await _api.GetLineStatusAsync(line);
            var andons = await _api.GetOpenAndonsAsync(line);
            _ui.Post(() =>
            {
                if (_line != line) return;   // the user switched line meanwhile
                Tiles.Clear();
                foreach (var m in status.Machines) Tiles.Add(new MachineTileViewModel(m));
                Andons.Clear();
                foreach (var a in andons) ApplyAndon(a);
                if (!keepError) ErrorMessage = string.Empty;
            });
        }
        catch (ApiException ex)
        {
            _ui.Post(() => ErrorMessage = ex.Message);
        }
    }

    private MachineTileViewModel? TileFor(string line, string machine)
        => line == _line ? Tiles.FirstOrDefault(t => t.Code == machine) : null;

    // Insert, update or remove one row, never going back to an older version of the call.
    private void ApplyAndon(AndonDto dto)
    {
        var version = AndonVersions.Of(dto);
        if (_seen.TryGetValue(dto.Id, out var seen) && version < seen) return;
        _seen[dto.Id] = version;

        var row = Andons.FirstOrDefault(r => r.Id == dto.Id);
        if (!AndonVersions.IsOpen(dto))
        {
            if (row is not null) Andons.Remove(row);
        }
        else if (row is null) Andons.Add(new AndonRowViewModel(dto, _time));
        else row.TryApply(dto);
    }

    [RelayCommand]
    private Task AcknowledgeAsync(AndonRowViewModel row) => RespondAsync(() => _api.AcknowledgeAndonAsync(row.Dto));

    [RelayCommand]
    private Task ResolveAsync(AndonRowViewModel row)
    {
        if (row.SelectedReason is not { } reason)
        {
            ErrorMessage = Strings.Dashboard_ChooseReason;
            return Task.CompletedTask;
        }
        return RespondAsync(() => _api.ResolveAndonAsync(row.Dto, reason.Id, null));
    }

    // 409 = someone else answered first: say so and reload, the list then shows who.
    private async Task RespondAsync(Func<Task<AndonDto>> call)
    {
        ErrorMessage = string.Empty;
        try
        {
            ApplyAndon(await call());
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            await LoadSnapshotAsync(keepError: true);
        }
    }

    [RelayCommand]
    private void DismissEscalation() => EscalationAlert = string.Empty;

    public async Task CloseAsync()
    {
        _realtime.Resync -= ResyncAsync;
        try { await _realtime.DisposeAsync(); }
        catch (Exception) { }   // closing the screen must never fail
    }
}
