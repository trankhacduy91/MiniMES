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

// What every station screen shares (scan screen, repair screen): the header, the full-screen result flash with
// its sound (spec M3 2.8), the offline state (2.9), change WO and logout. Each screen decides what a scan means.
public abstract partial class StationScreenViewModel(IExecutionApi execution, IKioskContext kiosk, IKioskNavigation navigation,
    IAuthClient auth, ITokenStore tokens, IKioskFeedback feedback, TimeProvider time) : ObservableObject
{
    public static readonly TimeSpan FlashDuration = TimeSpan.FromMilliseconds(1800);
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan PingTimeout = TimeSpan.FromSeconds(1);

    private int _flashVersion;

    protected IExecutionApi Execution => execution;
    protected IKioskContext Kiosk => kiosk;

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

    // Serial + the server's own wording ("Expected step 20 ASSY; ...") under the translated reason.
    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private bool _isOffline;

    partial void OnIsOfflineChanged(bool value) => OnOfflineChanged();

    // The screen's scan command re-evaluates CanExecute here.
    protected virtual void OnOfflineChanged() { }

    // The scanned text in one canonical form (scanners differ in suffix and case); the box is emptied at once.
    protected string TakeScanInput()
    {
        var serialNo = ScanInput.Trim().ToUpperInvariant();
        ScanInput = string.Empty;
        return serialNo;
    }

    // Sends one scan. null = no usable answer; the screen already shows why (offline or refused request).
    protected async Task<ScanResponse?> SendScanAsync(string serialNo, string result = "OK", string? defectCode = null)
    {
        if (kiosk.Station is not { } station) return null;
        try
        {
            using var timeout = new CancellationTokenSource(ScanTimeout, time);
            return await execution.ScanAsync(station.Id, new ScanRequest(serialNo, result, defectCode), timeout.Token);
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
        return null;
    }

    // Blocked answer: stable code -> translated text; the server's message stays as detail (and as fallback).
    protected void ShowBlocked(ScanResponse answer, string serialNo)
    {
        var reason = answer.ReasonCode is { } code ? Strings.ResourceManager.GetString("Block_" + code, Strings.Culture) : null;
        Flash(ScanState.Blocked, Strings.Scan_Blocked, reason ?? answer.Message, serialNo, reason is null ? string.Empty : answer.Message);
    }

    protected void Flash(ScanState state, string headline, string message, string serialNo, string detail = "")
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
