using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Traceability;

namespace MiniMes.Client.Core.ViewModels.Traceability;

public enum TraceMode
{
    Serial,        // backward: which lots went into this board (tree)
    Lot,           // forward: which boards used this lot (list)
    SupplierLot,   // forward from a whole delivery of one item
}

// Spec M6 step 8, the QC "Trace" screen. Backward = a lazy tree (spec 2.6); forward = a flat list grouped by where the
// boards are now (the View groups by Location), Excel export, and "Hold lot" for the delivery. Holding serials and
// pallets belongs to M7: those buttons are shown disabled until then.
public partial class TraceViewModel(ITraceApi api, IDialogService dialogs, TimeProvider time) : ObservableObject
{
    private const string ExcelFilter = "Excel workbook (*.xlsx)|*.xlsx";

    public ObservableCollection<TraceNodeViewModel> RootNodes { get; } = [];
    public ObservableCollection<ForwardTraceRow> Serials { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerialMode), nameof(IsLotMode), nameof(IsSupplierLotMode))]
    private TraceMode _mode = TraceMode.Serial;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TraceCommand))]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TraceCommand))]
    private string _itemCode = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTree), nameof(ShowList))]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand), nameof(HoldLotCommand))]
    private ForwardTraceResultDto? _forward;

    [ObservableProperty]
    private bool _showingBackward;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // The three radio buttons (WPF binds IsChecked to a bool).
    public bool IsSerialMode { get => Mode == TraceMode.Serial; set { if (value) Mode = TraceMode.Serial; } }
    public bool IsLotMode { get => Mode == TraceMode.Lot; set { if (value) Mode = TraceMode.Lot; } }
    public bool IsSupplierLotMode { get => Mode == TraceMode.SupplierLot; set { if (value) Mode = TraceMode.SupplierLot; } }

    public bool ShowTree => ShowingBackward;
    public bool ShowList => Forward is not null;

    partial void OnModeChanged(TraceMode value) => TraceCommand.NotifyCanExecuteChanged();

    partial void OnShowingBackwardChanged(bool value) => OnPropertyChanged(nameof(ShowTree));

    private bool CanTrace() => !string.IsNullOrWhiteSpace(SearchText) && (Mode != TraceMode.SupplierLot || !string.IsNullOrWhiteSpace(ItemCode));

    [RelayCommand(CanExecute = nameof(CanTrace))]
    private async Task TraceAsync()
    {
        ErrorMessage = ResultMessage = Summary = string.Empty;
        RootNodes.Clear();
        Serials.Clear();
        Forward = null;
        ShowingBackward = false;
        var key = SearchText.Trim().ToUpperInvariant();

        if (Mode == TraceMode.Serial)
        {
            // The board is the root, opened at once: its lots load like any other level.
            var root = TraceNodeViewModel.Serial(api, key, time.LocalTimeZone);
            RootNodes.Add(root);
            ShowingBackward = true;
            root.IsExpanded = true;
            await root.Loading;
            return;
        }

        try
        {
            var result = Mode == TraceMode.Lot
                ? await api.GetForwardAsync(key, null, null)
                : await api.GetForwardAsync(null, SearchText.Trim(), ItemCode.Trim());
            foreach (var s in result.Serials) Serials.Add(new ForwardTraceRow(s, LocationText(s.Location), Local(s.ConsumedAt)));
            Summary = result.Serials.Count == 0
                ? Strings.Trace_NothingFound
                : string.Format(CultureInfo.InvariantCulture, Strings.Trace_Summary, result.Serials.Count,
                    string.Join("  ·  ", result.ByLocation.Select(l => $"{LocationText(l.Location)} {l.Count.ToString("N0", CultureInfo.InvariantCulture)}")),
                    string.Join(", ", result.StartLots));
            Forward = result;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.FieldErrors.Count > 0 ? string.Join(" ", ex.FieldErrors.SelectMany(f => f.Value)) : ex.Message;
        }
    }

    private bool HasForward() => Forward is { Serials.Count: > 0 };

    [RelayCommand(CanExecute = nameof(HasForward))]
    private async Task ExportAsync()
    {
        var (lot, supplier, item) = Query();
        if (dialogs.PickFileToSave($"trace_{lot ?? supplier}.xlsx", ExcelFilter) is not { } path) return;
        try
        {
            await File.WriteAllBytesAsync(path, await api.GetForwardExcelAsync(lot, supplier, item));
            ResultMessage = string.Format(CultureInfo.InvariantCulture, Strings.Trace_Exported, path);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    // The delivery of the searched lot (or of the supplier lot) and the lots split from it. Asks first: only an NCR
    // decision (M7) releases a Hold.
    [RelayCommand(CanExecute = nameof(HasStartLot))]
    private async Task HoldLotAsync()
    {
        var lot = Forward!.StartLots[0];
        if (!dialogs.Confirm(string.Format(CultureInfo.InvariantCulture, Strings.Trace_ConfirmHold, lot))) return;
        try
        {
            var result = await api.HoldLotAsync(lot);
            ResultMessage = string.Format(CultureInfo.InvariantCulture, Strings.Trace_Held,
                result.Held.Count == 0 ? "-" : string.Join(", ", result.Held),
                result.Skipped.Count == 0 ? "-" : string.Join(", ", result.Skipped.Select(s => $"{s.LotNo} ({s.Status})")));
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private bool HasStartLot() => Forward is { StartLots.Count: > 0 };

    private (string? Lot, string? Supplier, string? Item) Query() => Mode == TraceMode.Lot
        ? (SearchText.Trim().ToUpperInvariant(), null, null)
        : (null, SearchText.Trim(), ItemCode.Trim());

    public static string LocationText(string location)
        => Strings.ResourceManager.GetString("Trace_Location_" + location, Strings.Culture) ?? location;

    private string Local(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), time.LocalTimeZone)
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

// LocationText groups the grid (the View's CollectionViewSource groups by it).
public sealed record ForwardTraceRow(ForwardTraceDto Item, string LocationText, string ConsumedText);
