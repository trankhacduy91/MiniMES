using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Material;

namespace MiniMes.Client.Core.ViewModels.Kiosk;

// What the next text in the scan box means. The scanner is the only input (gloves, no typing in other boxes, the
// focus never leaves the scan box), so the screen asks for one thing at a time.
public enum MaterialInput
{
    Lot,              // a reel's label
    SupervisorBadge,  // after a FIFO warning and a reason chosen: the supervisor's badge approves
    Remaining,        // taking a reel off: what is really left (typed on the numpad, or Empty / Keep buttons)
}

// Spec M6 step 4, kiosk "Load material": the checklist of what the open WO needs at this station and which reel is
// on it; scan a reel label -> green (loaded), yellow (an older reel exists: load that one, or a supervisor approves
// with a reason), red (wrong item, wrong step, hold, not IQC-passed, empty, expired: no override).
public partial class LoadMaterialViewModel(IExecutionApi execution, IMaterialApi material, IKioskContext kiosk,
    IKioskNavigation navigation, IAuthClient auth, ITokenStore tokens, IKioskFeedback feedback, TimeProvider time)
    : StationScreenViewModel(execution, kiosk, navigation, auth, tokens, feedback, time), ILoadable
{
    private readonly IKioskNavigation _navigation = navigation;

    public ObservableCollection<MaterialRow> Materials { get; } = [];

    // Preset reasons: tapped, not typed (spec 2.4 "supervisor quẹt thẻ rồi nhập lý do").
    public IReadOnlyList<string> OverrideReasons { get; } =
        [Strings.Material_ReasonOlderSuspect, Strings.Material_ReasonCustomer, Strings.Material_ReasonOlderUnreachable];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Prompt))]
    private MaterialInput _input = MaterialInput.Lot;

    // The yellow answer waiting for a decision: the reel scanned and the older one FIFO points to.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverrideOpen), nameof(OverrideText), nameof(Prompt))]
    private string? _pendingLotNo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverrideText))]
    private string? _suggestedLotNo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Prompt))]
    private string? _overrideReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnloadOpen), nameof(Prompt))]
    private MaterialRow? _unloadTarget;

    public bool IsOverrideOpen => PendingLotNo is not null;

    public bool IsUnloadOpen => UnloadTarget is not null;

    public string OverrideText => PendingLotNo is null ? string.Empty
        : string.Format(Strings.Material_FifoPanel, SuggestedLotNo, PendingLotNo);

    public string Prompt => Input switch
    {
        MaterialInput.SupervisorBadge => string.Format(Strings.Material_PromptBadge, OverrideReason),
        MaterialInput.Remaining => string.Format(Strings.Material_PromptRemaining, UnloadTarget?.Material.LotNo, UnloadTarget?.Material.Uom),
        _ => IsOverrideOpen ? Strings.Material_PromptReason : Strings.Material_PromptLot,
    };

    // Never throws: the checklist of the open WO at this station.
    public async Task LoadAsync()
    {
        if (Kiosk.Station is not { } station) return;
        try { Show(await material.GetStationMaterialsAsync(station.Id)); }
        catch (ApiException ex) { Flash(ScanState.Blocked, Strings.Scan_Blocked, ex.Message, string.Empty); }
    }

    private bool CanSubmit() => !IsOffline;

    protected override void OnOfflineChanged() => SubmitCommand.NotifyCanExecuteChanged();

    // The scanner's Enter (or the numpad's): what it means depends on Input.
    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        var text = TakeScanInput();
        if (text.Length == 0) return;
        switch (Input)
        {
            case MaterialInput.SupervisorBadge:
                await LoadLotAsync(PendingLotNo!, text, OverrideReason);
                break;
            case MaterialInput.Remaining:
                if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var left) || left < 0)
                {
                    Flash(ScanState.Blocked, Strings.Scan_Blocked, Strings.Material_RemainingNumber, text);
                    return;
                }
                await UnloadAsync(left);
                break;
            default:
                CancelOverride();   // a new reel scanned instead of deciding: the old question is gone
                await LoadLotAsync(text, null, null);
                break;
        }
    }

    private async Task LoadLotAsync(string lotNo, string? badge, string? reason)
    {
        if (Kiosk.Station is not { } station) return;
        var answer = await CallAsync(() => material.LoadAsync(station.Id, new LoadMaterialRequest(lotNo, null, badge, reason)), lotNo);
        if (answer is null) return;
        switch (answer.Outcome)
        {
            case "Accepted":
                CancelOverride();
                Flash(ScanState.Ok, Strings.Material_Loaded, answer.Loaded is { FifoOverrideBy: { } by }
                    ? string.Format(Strings.Material_LoadedApproved, answer.Loaded.ItemCode, by)
                    : string.Format(Strings.Material_LoadedItem, answer.Loaded?.ItemCode), lotNo);
                await LoadAsync();
                break;
            case "Warning":
                PendingLotNo = lotNo;
                SuggestedLotNo = answer.SuggestedLotNo;
                Input = MaterialInput.Lot;   // first a reason (buttons), then the badge
                Flash(ScanState.Warning, Strings.Material_Fifo, string.Format(Strings.Material_FifoOlder, answer.SuggestedLotNo), lotNo);
                break;
            default:
                var reason2 = Strings.ResourceManager.GetString("Load_" + answer.Code, Strings.Culture);
                Flash(ScanState.Blocked, Strings.Material_Rejected, reason2 ?? answer.Message, lotNo, reason2 is null ? string.Empty : answer.Message);
                break;
        }
    }

    [RelayCommand]
    private void ChooseReason(string reason)
    {
        OverrideReason = reason;
        Input = MaterialInput.SupervisorBadge;
    }

    [RelayCommand]
    private void CancelOverride()
    {
        PendingLotNo = SuggestedLotNo = OverrideReason = null;
        if (Input == MaterialInput.SupervisorBadge) Input = MaterialInput.Lot;
    }

    // R10: taking a reel off. The count is typed in the scan box; Empty = 0 left; Keep = the theoretical number stays.
    [RelayCommand]
    private void StartUnload(MaterialRow row)
    {
        CancelOverride();
        UnloadTarget = row;
        Input = MaterialInput.Remaining;
    }

    [RelayCommand]
    private Task UnloadEmptyAsync() => UnloadAsync(0);

    [RelayCommand]
    private Task UnloadKeepAsync() => UnloadAsync(null);

    [RelayCommand]
    private void CancelUnload()
    {
        UnloadTarget = null;
        Input = MaterialInput.Lot;
    }

    private async Task UnloadAsync(decimal? left)
    {
        if (Kiosk.Station is not { } station || UnloadTarget?.Material.LoadId is not { } loadId) return;
        var lotNo = UnloadTarget.Material.LotNo ?? string.Empty;
        var list = await CallAsync(() => material.UnloadAsync(station.Id, loadId, new UnloadMaterialRequest(left)), lotNo);
        if (list is null) return;
        CancelUnload();
        Show(list);
        Flash(ScanState.Ok, Strings.Material_Unloaded, left is { } l ? string.Format(Strings.Material_UnloadedLeft, l) : string.Empty, lotNo);
    }

    [RelayCommand]
    private void Back() => _navigation.ShowScan();

    private void Show(IEnumerable<StationMaterialDto> rows)
    {
        Materials.Clear();
        foreach (var r in rows) Materials.Add(new MaterialRow(r));
    }

    // Like a scan: no answer = offline; 400 (a badge that is not a supervisor's), 409 (another kiosk at the same
    // instant), 422 (no WO open, unloaded twice) are shown in red with the server's words.
    private async Task<T?> CallAsync<T>(Func<Task<T>> call, string what) where T : class
    {
        try
        {
            return await call();
        }
        catch (ApiException net) when (net.IsNetworkError)
        {
            IsOffline = true;
            Flash(ScanState.Blocked, Strings.Scan_NoResponse, Strings.Material_ResultUnknown, what);
        }
        catch (ApiException ex)
        {
            var message = ex.FieldErrors.Values.SelectMany(v => v).FirstOrDefault() ?? ex.Message;
            Flash(ScanState.Blocked, Strings.Material_Rejected, message, what);
            if (ex.Status == HttpStatusCode.Conflict) await LoadAsync();
        }
        return null;
    }

}

public sealed record MaterialRow(StationMaterialDto Material)
{
    public bool IsMissing => Material.Required && Material.LotNo is null;
    public bool CanUnload => Material.LoadId is not null;
}
