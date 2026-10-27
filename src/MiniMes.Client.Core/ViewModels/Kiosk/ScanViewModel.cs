using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.Kiosk;

// Spec M3 steps 7-8: the scan screen of a production station. The barcode scanner types the serial + Enter into
// the scan box; the answer is shown as a full-screen colour with a big word and a sound.
// At an inspection step (Test) the operator can report NG: tap NG, tap the defect, then scan the board, so the
// scanner stays the only way a serial enters (no typing, the focus never leaves the scan box).
// M4 step 8: the Andon panel (button + state of the station's machines) sits on this screen.
public partial class ScanViewModel(IExecutionApi execution, IWorkOrderApi workOrders, IKioskContext kiosk,
    IKioskNavigation navigation, IAuthClient auth, ITokenStore tokens, IKioskFeedback feedback, TimeProvider time,
    AndonPanelViewModel andon)
    : StationScreenViewModel(execution, kiosk, navigation, auth, tokens, feedback, time), ILoadable, IClosable
{
    public AndonPanelViewModel Andon { get; } = andon;

    public Task CloseAsync() => Andon.CloseAsync();

    private string? _sopUrl;

    // [v2] Yellow banner while the WO's first article waits for QC: only the sample may run.
    [ObservableProperty]
    private bool _isFaiPending;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSopCommand))]
    private bool _hasSop;

    [ObservableProperty]
    private bool _isSopOpen;

    // Local copy of the SOP file shown by the overlay (the API needs the token, a browser control does not send it).
    [ObservableProperty]
    private string? _sopFile;

    // Step 8: the station's routing step is an inspection (IsInspection): NG can be reported here.
    [ObservableProperty]
    private bool _canReportNg;

    public ObservableCollection<DefectCodeDto> DefectCodes { get; } = [];

    [ObservableProperty]
    private bool _isNgPanelOpen;

    // The defect chosen for the next scan; null = the next scan is OK.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ArmedText))]
    [NotifyPropertyChangedFor(nameof(IsNgArmed))]
    private DefectCodeDto? _armedDefect;

    public bool IsNgArmed => ArmedDefect is not null;

    public string ArmedText => ArmedDefect is { } d ? string.Format(Strings.Scan_NgArmed, d.Code, d.Name) : string.Empty;

    // Never throws: SOP, FAI banner and NG panel of the WO; the screen scans without them.
    public async Task LoadAsync()
    {
        _ = Andon.StartAsync();   // never throws; independent of the work order
        if (Kiosk.WorkOrder is null) return;
        try
        {
            var wo = await workOrders.GetWorkOrderAsync(Kiosk.WorkOrder.WorkOrderId);
            IsFaiPending = wo.FaiStatus == "Pending";
            var steps = wo.Operations.Where(o => o.WorkCenterId == Kiosk.Station?.WorkCenterId).ToList();
            _sopUrl = steps.Select(o => o.SopUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            HasSop = _sopUrl is not null;
            CanReportNg = steps.Any(o => o.IsInspection);
            if (CanReportNg)
                foreach (var code in await Execution.GetDefectCodesAsync()) DefectCodes.Add(code);
        }
        catch (ApiException) { }
    }

    private bool CanScan() => !IsOffline;

    protected override void OnOfflineChanged() => ScanCommand.NotifyCanExecuteChanged();

    // Concurrent on purpose: a fast scanner can send the next serial before the API answered the previous one;
    // the default (no concurrency) would make CanExecute false meanwhile and the scanner's Enter would be lost.
    [RelayCommand(CanExecute = nameof(CanScan), AllowConcurrentExecutions = true)]
    private async Task ScanAsync()
    {
        var serialNo = TakeScanInput();
        if (serialNo.Length == 0) return;
        IsSopOpen = false;   // a scan while the SOP is open: close it and handle the scan
        IsNgPanelOpen = false;
        // One NG per arming: the next board is OK again unless the operator taps NG once more.
        var defect = ArmedDefect;
        ArmedDefect = null;

        var answer = await SendScanAsync(serialNo, defect is null ? "OK" : "NG", defect?.Code);
        if (answer is null) return;
        switch (answer.Result)
        {
            case "Ok":
                var text = answer.Duplicate ? Strings.Scan_Duplicate
                    : answer.NextStepName is { } next ? string.Format(Strings.Scan_Next, next) : Strings.Scan_Completed;
                Flash(ScanState.Ok, Strings.Scan_Ok, text, serialNo);
                if (IsFaiPending) _ = LoadFaiAsync();   // the sample moved on; QC may have approved meanwhile
                break;
            case "Ng":
                Flash(ScanState.Ng, Strings.Scan_Ng, string.Format(Strings.Scan_NgRecorded, defect?.Code), serialNo);
                break;
            default:
                ShowBlocked(answer, serialNo);
                if (answer.ReasonCode == "FaiNotApproved") IsFaiPending = true;
                break;
        }
    }

    private async Task LoadFaiAsync()
    {
        try { IsFaiPending = (await workOrders.GetWorkOrderAsync(Kiosk.WorkOrder!.WorkOrderId)).FaiStatus == "Pending"; }
        catch (ApiException) { }
    }

    [RelayCommand]
    private void OpenNgPanel() => IsNgPanelOpen = true;

    [RelayCommand]
    private void ChooseDefect(DefectCodeDto defect)
    {
        ArmedDefect = defect;
        IsNgPanelOpen = false;
    }

    [RelayCommand]
    private void CancelNg()
    {
        ArmedDefect = null;
        IsNgPanelOpen = false;
    }

    private bool CanOpenSop() => HasSop;

    [RelayCommand(CanExecute = nameof(CanOpenSop))]
    private async Task OpenSopAsync()
    {
        if (_sopUrl is null) return;
        try
        {
            var bytes = await Execution.GetSopAsync(_sopUrl);
            var file = Path.Combine(Path.GetTempPath(), "MiniMes", "sop", Path.GetFileName(_sopUrl));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllBytesAsync(file, bytes);
            SopFile = file;
            IsSopOpen = true;
        }
        catch (ApiException ex)
        {
            Flash(ScanState.Blocked, Strings.Scan_Sop, ex.Message, string.Empty);
        }
    }

    [RelayCommand]
    private void CloseSop() => IsSopOpen = false;
}
