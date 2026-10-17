using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Execution;

namespace MiniMes.Client.Core.ViewModels.Kiosk;

// Spec M3 step 7: the scan screen. The barcode scanner types the serial + Enter into the scan box; the answer is
// shown as a full-screen colour with a big word and a sound, then the screen returns to READY by itself.
// Offline (health probe or a scan without answer): red banner, scanning disabled, no local queue (spec 2.9).
public partial class ScanViewModel(IExecutionApi execution, IWorkOrderApi workOrders, IKioskContext kiosk,
    IKioskNavigation navigation, IAuthClient auth, ITokenStore tokens, IKioskFeedback feedback, TimeProvider time)
    : ObservableObject, ILoadable
{
    public static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(1800);
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);

    private int _flashVersion;
    private string? _sopUrl;

    public string StationCode => kiosk.Station?.Code ?? string.Empty;

    public string WorkOrderText => kiosk.WorkOrder is { } wo ? string.Format(Strings.Scan_WorkOrder, wo.WoNo, wo.ItemCode) : string.Empty;

    public string OperatorText => tokens.Session is { } s ? string.Format(Strings.Kiosk_Operator, s.FullName, s.EmployeeCode) : string.Empty;

    // PropertyChanged binding: the full scanned text is here before the scanner's Enter arrives.
    [ObservableProperty]
    private string _scanInput = string.Empty;

    [ObservableProperty]
    private ScanState _state = ScanState.Idle;

    [ObservableProperty]
    private string _headline = Strings.Scan_Ready;

    [ObservableProperty]
    private string _message = string.Empty;

    // The server's own wording ("Expected step 20 ASSY; ...") under the translated reason.
    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isOffline;

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

    // Never throws: SOP and FAI banner of the WO; the screen works without them.
    public async Task LoadAsync()
    {
        if (kiosk.WorkOrder is null) return;
        try
        {
            var wo = await workOrders.GetWorkOrderAsync(kiosk.WorkOrder.WorkOrderId);
            IsFaiPending = wo.FaiStatus == "Pending";
            _sopUrl = wo.Operations.Where(o => o.WorkCenterId == kiosk.Station?.WorkCenterId)
                .Select(o => o.SopUrl).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));
            HasSop = _sopUrl is not null;
        }
        catch (ApiException) { }
    }

    private bool CanScan() => !IsOffline;

    // Concurrent on purpose: a fast scanner can send the next serial before the API answered the previous one;
    // the default (no concurrency) would silently drop it.
    [RelayCommand(CanExecute = nameof(CanScan), AllowConcurrentExecutions = true)]
    private async Task ScanAsync()
    {
        // Scanners differ in suffix and case: send one canonical form; empty the box at once for the next scan.
        var serialNo = ScanInput.Trim().ToUpperInvariant();
        ScanInput = string.Empty;
        if (serialNo.Length == 0 || kiosk.Station is not { } station) return;
        IsSopOpen = false;   // a scan while the SOP is open: close it and handle the scan

        try
        {
            using var timeout = new CancellationTokenSource(ScanTimeout, time);
            var answer = await execution.ScanAsync(station.Id, new ScanRequest(serialNo), timeout.Token);
            Show(answer, serialNo);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is ApiException { IsNetworkError: true })
        {
            // Sent but no answer: maybe recorded, maybe not. Scanning again is safe (duplicate detection).
            IsOffline = true;
            Flash(ScanState.Blocked, Strings.Scan_NoResponse, Strings.Scan_ResultUnknown, serialNo);
        }
        catch (ApiException ex)
        {
            Flash(ScanState.Blocked, Strings.Scan_Blocked, ex.Message, serialNo);   // 400, 401, 404...
        }
    }

    private void Show(ScanResponse answer, string serialNo)
    {
        switch (answer.Result)
        {
            case "Ok":
                var text = answer.Duplicate ? Strings.Scan_Duplicate
                    : answer.NextStepName is { } next ? string.Format(Strings.Scan_Next, next) : Strings.Scan_Completed;
                Flash(ScanState.Ok, Strings.Scan_Ok, text, serialNo);
                if (IsFaiPending) _ = LoadAsync();   // the sample moved on; QC may have approved meanwhile
                break;
            case "Ng":
                Flash(ScanState.Ng, Strings.Scan_Ng, answer.Message, serialNo);
                break;
            default:
                // Stable code -> translated text; the server's message stays as detail (and as fallback).
                var reason = answer.ReasonCode is { } code ? Strings.ResourceManager.GetString("Block_" + code, Strings.Culture) : null;
                Flash(ScanState.Blocked, Strings.Scan_Blocked, reason ?? answer.Message, serialNo, reason is null ? string.Empty : answer.Message);
                if (answer.ReasonCode == "FaiNotApproved") IsFaiPending = true;
                break;
        }
    }

    private void Flash(ScanState state, string headline, string message, string serialNo, string detail = "")
    {
        var version = ++_flashVersion;
        State = state;
        Headline = headline;
        Message = message;
        Detail = string.IsNullOrEmpty(detail) ? serialNo : $"{serialNo} - {detail}";
        feedback.Play(state);
        _ = BackToIdleAsync(version);
    }

    // A newer scan owns the screen: only the latest flash may reset it, or a fast second scan would vanish early.
    private async Task BackToIdleAsync(int version)
    {
        await Task.Delay(FlashDuration, time);
        if (version != _flashVersion) return;
        State = ScanState.Idle;
        Headline = Strings.Scan_Ready;
        Message = Detail = string.Empty;
    }

    // Called by the view every few seconds. Online again -> the banner disappears and scanning resumes.
    [RelayCommand]
    private async Task CheckConnectionAsync()
    {
        using var timeout = new CancellationTokenSource(PingTimeout, time);
        IsOffline = !await execution.PingAsync(timeout.Token);
    }

    private bool CanOpenSop() => HasSop;

    [RelayCommand(CanExecute = nameof(CanOpenSop))]
    private async Task OpenSopAsync()
    {
        if (_sopUrl is null) return;
        try
        {
            var bytes = await execution.GetSopAsync(_sopUrl);
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

    // Another WO at this station: the next session opened replaces this one on the server.
    [RelayCommand]
    private void ChangeWorkOrder() => navigation.ShowHome();

    // End of shift: close the session so nobody scans under this operator's name, then forget the token.
    // Best effort: if the server cannot be reached, the next login at this station closes it anyway.
    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (kiosk is { Station: { } station, Session: { } session })
        {
            try { await execution.CloseSessionAsync(station.Id, session.Id); }
            catch (ApiException) { }
        }
        kiosk.Clear();
        auth.Logout();
        navigation.ShowLogin();
    }
}
