using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Quality;

namespace MiniMes.Client.Core.ViewModels.Quality;

// Spec M7 step 9: the Hold dialog, shown as a panel over the trace screen ("Hold all" of a lot), the SPC chart of a
// machine ("Hold serials in window") and the Holds screen (serials, carton, pallet). Preview first ("87 boards: 55
// InProcess, 24 Completed..."), then a reason (Q9) and, if wanted, a new NCR. Any change of the target clears the
// preview: what is held is always what was shown.
public partial class HoldPanelViewModel(IHoldApi api, TimeProvider time) : ObservableObject
{
    public const string TimeFormat = "yyyy-MM-dd HH:mm";

    public IReadOnlyList<string> Scopes { get; } = ["Serials", "Carton", "Pallet", "Lot", "MachineWindow"];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _canChooseScope;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWindow), nameof(IsSerialList), nameof(ReferenceLabel))]
    private string _scope = "Serials";

    [ObservableProperty] private string _reference = string.Empty;   // lot / carton / pallet / machine; serials one per line
    [ObservableProperty] private string _fromText = string.Empty;    // plant time, MachineWindow only
    [ObservableProperty] private string _toText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(HoldCommand))]
    private string _reason = string.Empty;

    [ObservableProperty] private bool _createNcr;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewText), nameof(WarningText), nameof(HasPreview))]
    [NotifyCanExecuteChangedFor(nameof(HoldCommand))]
    private HoldPreviewDto? _preview;

    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _resultMessage = string.Empty;

    // The host refreshes what it shows (trace list, holds list).
    public event Action<HoldResultDto>? Held;

    public bool IsWindow => Scope == "MachineWindow";
    public bool IsSerialList => Scope == "Serials";
    public bool HasPreview => Preview is not null;

    public string ReferenceLabel => Strings.ResourceManager.GetString("Hold_Ref_" + Scope, Strings.Culture) ?? Scope;

    public string PreviewText => Preview is not { } p ? string.Empty
        : string.Format(CultureInfo.CurrentCulture, Strings.Hold_PreviewText, p.SerialCount,
            p.SerialsByStatus.Count == 0 ? "-" : string.Join(", ", p.SerialsByStatus.Select(s => $"{s.Count} {s.Status}")),
            p.Lots.Count == 0 ? "-" : string.Join(", ", p.Lots),
            p.SkippedLots.Count == 0 ? "-" : string.Join(", ", p.SkippedLots.Select(s => $"{s.LotNo} ({s.Status})")));

    public string WarningText => Preview is not { } p ? string.Empty
        : string.Join(" ", new[]
        {
            p.Shipped > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Hold_ShippedWarning, p.Shipped) : null,
            p.SkippedScrapped > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Hold_ScrappedSkipped, p.SkippedScrapped) : null,
        }.Where(t => t is not null));

    // Opened by a screen with the target already known; the preview loads at once.
    public async Task OpenAsync(string scope, string reference, DateTime? fromUtc = null, DateTime? toUtc = null, bool canChooseScope = false)
    {
        (Scope, Reference, CanChooseScope) = (scope, reference, canChooseScope);
        FromText = fromUtc is { } f ? Local(f).ToString(TimeFormat, CultureInfo.InvariantCulture) : string.Empty;
        ToText = toUtc is { } t ? Local(t).ToString(TimeFormat, CultureInfo.InvariantCulture) : string.Empty;
        (Reason, CreateNcr, ErrorMessage, ResultMessage, Preview) = (string.Empty, false, string.Empty, string.Empty, null);
        IsOpen = true;
        if (!string.IsNullOrWhiteSpace(reference)) await PreviewAsync();
    }

    partial void OnScopeChanged(string value) => Preview = null;
    partial void OnReferenceChanged(string value) => Preview = null;
    partial void OnFromTextChanged(string value) => Preview = null;
    partial void OnToTextChanged(string value) => Preview = null;

    [RelayCommand]
    public async Task PreviewAsync()
    {
        (ErrorMessage, ResultMessage) = (string.Empty, string.Empty);
        if (Request() is not { } request) return;
        try
        {
            Preview = await api.PreviewAsync(request);
        }
        catch (ApiException ex)
        {
            Preview = null;
            ErrorMessage = Message(ex);
        }
    }

    private bool CanHold() => Preview is { } p && (p.SerialCount > 0 || p.Lots.Count > 0) && !string.IsNullOrWhiteSpace(Reason);

    [RelayCommand(CanExecute = nameof(CanHold))]
    private async Task HoldAsync()
    {
        if (Request() is not { } request) return;
        ErrorMessage = string.Empty;
        try
        {
            var result = await api.HoldAsync(request with { Reason = Reason.Trim(), CreateNcr = CreateNcr });
            ResultMessage = string.Format(CultureInfo.CurrentCulture, Strings.Hold_Done, result.Serials, result.Lots,
                result.NcrNo is { } no ? string.Format(CultureInfo.CurrentCulture, Strings.Hold_NcrOpened, no) : string.Empty);
            Preview = null;   // done: holding again needs a new preview
            Held?.Invoke(result);
        }
        catch (ApiException ex)
        {
            ErrorMessage = Message(ex);
        }
    }

    [RelayCommand]
    private void Close() => IsOpen = false;

    // Null = the times cannot be read (message shown).
    private HoldTargetsRequest? Request()
    {
        var reference = Reference.Trim();
        switch (Scope)
        {
            case "Serials":
                return new HoldTargetsRequest(Scope, SerialNos: reference.Split(['\r', '\n', ',', ';', ' '],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList());
            case "Lot": return new HoldTargetsRequest(Scope, LotNo: reference);
            case "Carton": return new HoldTargetsRequest(Scope, CartonNo: reference);
            case "Pallet": return new HoldTargetsRequest(Scope, PalletNo: reference);
        }
        if (!TryUtc(FromText, out var from) || !TryUtc(ToText, out var to))
        {
            ErrorMessage = string.Format(CultureInfo.CurrentCulture, Strings.Hold_BadTime, TimeFormat);
            return null;
        }
        return new HoldTargetsRequest(Scope, MachineCode: reference, From: from, To: to);
    }

    private bool TryUtc(string text, out DateTime utc)
    {
        utc = default;
        if (!DateTime.TryParseExact(text.Trim(), TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) return false;
        utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), time.LocalTimeZone);
        return true;
    }

    private DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), time.LocalTimeZone);

    private static string Message(ApiException ex)
        => ex.FieldErrors.Count > 0 ? string.Join(" ", ex.FieldErrors.SelectMany(f => f.Value)) : ex.Message;
}
