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
using MiniMes.Contracts.Packing;

namespace MiniMes.Client.Core.ViewModels.Kiosk;

public enum PackingMode
{
    Pack,        // scan boards into the open carton
    Palletize,   // scan carton labels onto the open pallet
}

// What the next text in the scan box means: a code (board or carton label), or, after a partial-close reason was
// tapped, the supervisor's badge (the kiosk's token is an operator's; same flow as the FIFO exception of M6).
public enum PackingInput
{
    Code,
    SupervisorBadge,
}

// One line of the list under the progress: a board in the carton, or a carton on the pallet. Newest first.
public sealed record PackingLine(string Code, string Text, bool OnHold = false);

// Spec M3b step 6 / 2.5: the Packing screen of the Pack station. Two modes on one scan box: Pack (big "7 / 10", the
// open carton and its boards) and Palletize ("12 / 20", the open pallet and its cartons). A full carton or pallet
// closes on the server; this screen plays its own sound and sends the label the server made to the label folder.
public partial class PackingViewModel(IExecutionApi execution, IPackingApi packing, ILabelPrinter printer, IKioskContext kiosk,
    IKioskNavigation navigation, IAuthClient auth, ITokenStore tokens, IKioskFeedback feedback, TimeProvider time)
    : StationScreenViewModel(execution, kiosk, navigation, auth, tokens, feedback, time), ILoadable
{
    private readonly TimeProvider _time = time;

    public ObservableCollection<PackingLine> Lines { get; } = [];

    // Tapped, not typed (gloves): why the last carton/pallet is short.
    public IReadOnlyList<string> PartialReasons { get; } =
        [Strings.Packing_ReasonWoEnd, Strings.Packing_ReasonShiftEnd, Strings.Packing_ReasonLineChange];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPackMode), nameof(IsPalletizeMode), nameof(ContainerTitle), nameof(Prompt), nameof(PartialButtonText))]
    private PackingMode _mode = PackingMode.Pack;

    public bool IsPackMode => Mode == PackingMode.Pack;
    public bool IsPalletizeMode => Mode == PackingMode.Palletize;

    // The open carton (Pack) or pallet (Palletize); null = none open yet (the next scan opens one).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContainerTitle))]
    [NotifyCanExecuteChangedFor(nameof(OpenPartialCommand))]
    private string? _containerNo;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    [NotifyCanExecuteChangedFor(nameof(OpenPartialCommand))]
    private int _count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText))]
    private int? _capacity;

    public string ProgressText => Capacity is { } cap ? $"{Count} / {cap}" : Count.ToString(CultureInfo.InvariantCulture);

    public string ContainerTitle => ContainerNo is { } no
        ? string.Format(CultureInfo.InvariantCulture, IsPackMode ? Strings.Packing_Carton : Strings.Packing_Pallet, no)
        : IsPackMode ? Strings.Packing_NewCarton : Strings.Packing_NewPallet;

    public string PartialButtonText => IsPackMode ? Strings.Packing_ClosePartialCarton : Strings.Packing_ClosePartialPallet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Prompt))]
    private bool _isPartialOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Prompt))]
    private PackingInput _input = PackingInput.Code;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Prompt))]
    private string? _partialReason;

    public string Prompt => Input == PackingInput.SupervisorBadge
        ? string.Format(CultureInfo.InvariantCulture, Strings.Packing_PromptBadge, PartialReason)
        : IsPartialOpen ? Strings.Packing_PromptReason
        : IsPackMode ? Strings.Packing_PromptSerial : Strings.Packing_PromptCarton;

    // Never throws: what is open at this station for the session's WO.
    public async Task LoadAsync()
    {
        if (Kiosk.Station is not { } station) return;
        try { Show(await packing.GetStatusAsync(station.Id)); }
        catch (ApiException ex) when (ex.IsNetworkError) { IsOffline = true; }
        catch (ApiException ex) { Flash(ScanState.Blocked, Strings.Scan_Blocked, ex.Message, string.Empty); }
    }

    private void Show(PackingStatusDto status)
    {
        Lines.Clear();
        if (IsPackMode)
        {
            ContainerNo = status.OpenCarton?.CartonNo;
            Count = status.OpenCarton?.Qty ?? 0;
            Capacity = status.QtyPerCarton;
            foreach (var s in status.OpenCarton?.Serials ?? [])
                Lines.Add(new PackingLine(s.SerialNo, $"{s.SerialNo}   {Local(s.PackedAt)}"));
        }
        else
        {
            ContainerNo = status.OpenPallet?.PalletNo;
            Count = status.OpenPallet?.CartonCount ?? 0;
            Capacity = status.CartonsPerPallet;
            foreach (var c in status.OpenPallet?.Cartons ?? [])
                Lines.Add(new PackingLine(c.CartonNo, string.Format(CultureInfo.InvariantCulture, Strings.Packing_CartonLine,
                    c.CartonNo, c.Qty, c.IsPartial ? Strings.Packing_Partial : string.Empty), c.OnHold));
        }
    }

    private string Local(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _time.LocalTimeZone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    [RelayCommand]
    private async Task SwitchModeAsync(PackingMode mode)
    {
        if (Mode == mode) return;
        CancelPartial();
        Mode = mode;
        await LoadAsync();
    }

    private bool CanScan() => !IsOffline;

    protected override void OnOfflineChanged() => ScanCommand.NotifyCanExecuteChanged();

    // Concurrent on purpose, like the scan screen: the scanner's next Enter must not be lost while the API answers.
    [RelayCommand(CanExecute = nameof(CanScan), AllowConcurrentExecutions = true)]
    private async Task ScanAsync()
    {
        var text = TakeScanInput();
        if (text.Length == 0) return;
        if (Input == PackingInput.SupervisorBadge) await ClosePartialAsync(text);
        else if (IsPackMode) await PackAsync(text);
        else await PalletizeAsync(text);
    }

    private async Task PackAsync(string serialNo)
    {
        if (Kiosk.Station is not { } station) return;
        var answer = await CallAsync(ct => packing.PackAsync(station.Id, serialNo, ct), serialNo);
        if (answer is null) return;
        if (answer.Result != "Ok")
        {
            Blocked(answer.ReasonCode, answer.Message, serialNo);
            return;
        }
        var progress = $"{answer.CartonNo}  {answer.CartonQty} / {answer.QtyPerCarton}";
        if (answer.CartonClosed)
            Flash(ScanState.Full, Strings.Packing_CartonFull, await PrintAsync(answer.CartonNo!, answer.LabelZpl, answer.Message), serialNo);
        else
            Flash(ScanState.Ok, answer.Duplicate ? Strings.Packing_AlreadyPacked : Strings.Packing_Packed, progress, serialNo);
        await LoadAsync();
    }

    private async Task PalletizeAsync(string cartonNo)
    {
        if (Kiosk.Station is not { } station) return;
        var answer = await CallAsync(ct => packing.PalletizeAsync(station.Id, cartonNo, ct), cartonNo);
        if (answer is null) return;
        if (answer.Result != "Ok")
        {
            Blocked(answer.ReasonCode, answer.Message, cartonNo);
            return;
        }
        var progress = $"{answer.PalletNo}  {answer.CartonCount} / {answer.CartonsPerPallet}";
        if (answer.PalletClosed)
            Flash(ScanState.Full, Strings.Packing_PalletFull, await PrintAsync(answer.PalletNo!, answer.LabelZpl, answer.Message), cartonNo);
        else
            Flash(ScanState.Ok, answer.Duplicate ? Strings.Packing_AlreadyOnPallet : Strings.Packing_OnPallet, progress, cartonNo);
        await LoadAsync();
    }

    // The server made and logged the label; here it only goes to the label folder. No ZPL = the server could not make
    // it (its message says so): print it again from the label endpoint.
    private async Task<string> PrintAsync(string key, string? zpl, string serverMessage)
    {
        if (zpl is null) return serverMessage;
        try
        {
            var file = await printer.PrintAsync(key, zpl);
            return string.Format(CultureInfo.InvariantCulture, Strings.Packing_LabelPrinted, Path.GetFileName(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Format(CultureInfo.InvariantCulture, Strings.Packing_LabelFailed, ex.Message);
        }
    }

    // Stable code -> text from Strings.resx (Block_* like the scan screen); the server's wording stays as detail.
    private void Blocked(string? reasonCode, string message, string code)
    {
        var reason = reasonCode is { } rc ? Strings.ResourceManager.GetString("Block_" + rc, Strings.Culture) : null;
        Flash(ScanState.Blocked, Strings.Scan_Blocked, reason ?? message, code, reason is null ? string.Empty : message);
    }

    // ---------- partial carton / pallet: reason (buttons), then the supervisor's badge (scan) ----------

    private bool CanOpenPartial() => ContainerNo is not null && Count > 0;

    [RelayCommand(CanExecute = nameof(CanOpenPartial))]
    private void OpenPartial()
    {
        IsPartialOpen = true;
        Input = PackingInput.Code;
        PartialReason = null;
    }

    [RelayCommand]
    private void ChooseReason(string reason)
    {
        PartialReason = reason;
        Input = PackingInput.SupervisorBadge;
    }

    [RelayCommand]
    private void CancelPartial()
    {
        IsPartialOpen = false;
        Input = PackingInput.Code;
        PartialReason = null;
    }

    private async Task ClosePartialAsync(string badge)
    {
        if (ContainerNo is not { } no || PartialReason is not { } reason) return;
        var request = new ClosePartialRequest(reason, badge);
        string? key, zpl, message;
        if (IsPackMode)
        {
            var carton = await CallAsync(ct => packing.ClosePartialCartonAsync(no, request, ct), badge, keepBadgeStep: true);
            if (carton is null) return;
            (key, zpl, message) = (carton.CartonNo, carton.LabelZpl, string.Format(CultureInfo.InvariantCulture, Strings.Packing_CartonClosedShort, carton.CartonNo, carton.Qty, carton.ClosedBy));
        }
        else
        {
            var pallet = await CallAsync(ct => packing.ClosePartialPalletAsync(no, request, ct), badge, keepBadgeStep: true);
            if (pallet is null) return;
            (key, zpl, message) = (pallet.PalletNo, pallet.LabelZpl, string.Format(CultureInfo.InvariantCulture, Strings.Packing_PalletClosedShort, pallet.PalletNo, pallet.CartonCount, pallet.ClosedBy));
        }
        CancelPartial();
        var printed = await PrintAsync(key, zpl, Strings.Packing_LabelMissing);
        Flash(ScanState.Full, Strings.Packing_ClosedShortHeadline, $"{message} {printed}", key);
        await LoadAsync();
    }

    // Network loss: offline banner, the answer is unknown (scanning again is safe: duplicates are recognised).
    // A refusal: its reason on screen. A wrong badge (400) keeps the badge step so the supervisor can scan again;
    // anything else ends the partial close.
    private async Task<T?> CallAsync<T>(Func<CancellationToken, Task<T>> call, string what, bool keepBadgeStep = false) where T : class
    {
        try
        {
            using var timeout = new CancellationTokenSource(ScanTimeout, _time);
            return await call(timeout.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ex is ApiException { IsNetworkError: true })
        {
            IsOffline = true;
            Flash(ScanState.Blocked, Strings.Scan_NoResponse, Strings.Scan_ResultUnknown, what);
        }
        catch (ApiException ex)
        {
            var message = ex.FieldErrors.Values.SelectMany(v => v).FirstOrDefault() ?? ex.Message;
            Flash(ScanState.Blocked, Strings.Scan_Blocked, message, what);
            if (!(keepBadgeStep && ex.Status == HttpStatusCode.BadRequest)) CancelPartial();
            if (ex.Status == HttpStatusCode.Conflict) await LoadAsync();
        }
        return null;
    }
}
