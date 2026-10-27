using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Realtime;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Machines;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Machines;

namespace MiniMes.Client.Core.ViewModels.Kiosk;

public sealed record AndonTypeOption(string Code, string Label);

// One machine of the station in the kiosk banner: its live state and the Andon calls open on it.
public partial class StationMachineViewModel(int id, string code, string name) : ObservableObject
{
    private readonly Dictionary<string, AndonDto> _open = [];

    public int Id { get; } = id;
    public string Code { get; } = code;
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private MachineState? _state;

    private DateTime? _stateSince;

    public string StateText => State?.ToString() ?? Strings.Dashboard_NoData;

    // Highlighted in the "which machine?" step of the panel.
    [ObservableProperty]
    private bool _isSelected;

    // "Machine Andon: Acknowledged by sup01": the operator sees help is coming.
    [ObservableProperty]
    private string _andonText = string.Empty;

    public void Apply(MachineState? state, DateTime? since)
    {
        if (_stateSince is { } shown && since < shown) return;
        (State, _stateSince) = (state, since);
    }

    public void Apply(AndonDto dto)
    {
        if (_open.TryGetValue(dto.Type, out var shown) && AndonVersions.Of(dto) < AndonVersions.Of(shown)) return;
        if (AndonVersions.IsOpen(dto)) _open[dto.Type] = dto;
        else if (shown?.Id == dto.Id) _open.Remove(dto.Type);
        AndonText = string.Join("   ", _open.Values.OrderBy(a => a.Type).Select(a => string.Format(Strings.Andon_Status, a.Type,
            a.AckBy is { } by ? string.Format(Strings.Dashboard_AckBy, by) : a.Status)));
    }

    public void ClearAndons() => _open.Clear();
}

// Spec M4 step 8, kiosk: a big Andon button (choose the machine if the station has several, then the problem type)
// and a banner with the state of the station's machines. A station is linked to machines through its work center
// (user decision 27/10/2026): one machine is preselected, several are chosen by the operator, none = no Andon here.
public partial class AndonPanelViewModel : ObservableObject, IClosable
{
    private readonly IMachineApi _api;
    private readonly IRealtimeClient _realtime;
    private readonly IUiDispatcher _ui;
    private readonly IKioskContext _kiosk;
    private string? _line;

    public AndonPanelViewModel(IMachineApi api, IRealtimeClientFactory realtime, IUiDispatcher ui, IKioskContext kiosk)
    {
        _api = api;
        _realtime = realtime.Create();
        _ui = ui;
        _kiosk = kiosk;
        _realtime.MachineStateChanged += d => _ui.Post(() => Find(d.Machine)?.Apply(d.State, d.Since));
        _realtime.AndonChanged += d => _ui.Post(() => Find(d.Machine)?.Apply(d));
        _realtime.ConnectionChanged += connected => _ui.Post(() => IsConnected = connected);
        _realtime.Resync += LoadSnapshotAsync;
    }

    public ObservableCollection<StationMachineViewModel> Machines { get; } = [];

    public IReadOnlyList<AndonTypeOption> Types { get; } = AndonTypes.All
        .Select(t => new AndonTypeOption(t, Strings.ResourceManager.GetString("Andon_Type_" + t, Strings.Culture) ?? t)).ToList();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RaiseCommand))]
    private StationMachineViewModel? _selectedMachine;

    [ObservableProperty]
    private bool _hasMachines;

    // More than one machine: the panel asks which one first.
    public bool MustChooseMachine => Machines.Count > 1;

    [ObservableProperty]
    private bool _isPanelOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReconnecting))]
    private bool _isConnected = true;

    public bool IsReconnecting => !IsConnected;

    [ObservableProperty]
    private string _message = string.Empty;

    // Never throws: the scan screen works without the Andon panel.
    public async Task StartAsync()
    {
        if (_kiosk.Station is not { WorkCenterId: { } workCenterId } station)
        {
            Message = Strings.Andon_NoMachine;
            return;
        }
        try
        {
            var machines = await _api.GetMachinesAsync(workCenterId);
            _line = (await _api.GetLineAsync(station.LineId)).Code;
            foreach (var m in machines.OrderBy(m => m.Code)) Machines.Add(new StationMachineViewModel(m.Id, m.Code, m.Name));
        }
        catch (ApiException ex)
        {
            Message = ex.Message;
            return;
        }
        HasMachines = Machines.Count > 0;
        OnPropertyChanged(nameof(MustChooseMachine));
        if (!HasMachines)
        {
            Message = Strings.Andon_NoMachine;
            return;
        }
        if (Machines.Count == 1) SelectedMachine = Machines[0];

        await _realtime.JoinLineAsync(_line);
        await LoadSnapshotAsync();
        _ = _realtime.StartAsync();   // Resync reloads once connected
    }

    private async Task LoadSnapshotAsync()
    {
        if (_line is not { } line) return;
        try
        {
            var status = await _api.GetLineStatusAsync(line);
            var andons = await _api.GetOpenAndonsAsync(line);
            _ui.Post(() =>
            {
                foreach (var m in Machines)
                {
                    var s = status.Machines.FirstOrDefault(x => x.Id == m.Id);
                    m.Apply(s?.State, s?.StateSince);
                    m.ClearAndons();
                    foreach (var a in andons.Where(a => a.MachineId == m.Id)) m.Apply(a);
                }
            });
        }
        catch (ApiException) { }   // the banner keeps what it shows; the next push or resync repairs it
    }

    private StationMachineViewModel? Find(string machine) => Machines.FirstOrDefault(m => m.Code == machine);

    [RelayCommand]
    private void OpenPanel()
    {
        Message = string.Empty;
        if (Machines.Count > 1) SelectedMachine = null;   // ask every time: the last choice may be wrong now
        IsPanelOpen = true;
    }

    [RelayCommand]
    private void ClosePanel() => IsPanelOpen = false;

    // Buttons, not a ListBox: on the kiosk no control may take the keyboard focus from the scan box.
    [RelayCommand]
    private void ChooseMachine(StationMachineViewModel machine) => SelectedMachine = machine;

    partial void OnSelectedMachineChanged(StationMachineViewModel? oldValue, StationMachineViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    private bool CanRaise() => SelectedMachine is not null;

    [RelayCommand(CanExecute = nameof(CanRaise))]
    private async Task RaiseAsync(AndonTypeOption type)
    {
        if (SelectedMachine is not { } machine) return;
        try
        {
            var result = await _api.RaiseAndonAsync(machine.Id, type.Code);
            machine.Apply(result.Andon);
            Message = result.Created
                ? string.Format(Strings.Andon_Sent, type.Label, machine.Code)
                : string.Format(Strings.Andon_AlreadyOpen, type.Label, machine.Code, result.Andon.Status);
            IsPanelOpen = false;
        }
        catch (ApiException ex)
        {
            Message = ex.Message;
        }
    }

    public async Task CloseAsync()
    {
        _realtime.Resync -= LoadSnapshotAsync;
        try { await _realtime.DisposeAsync(); }
        catch (Exception) { }
    }
}
