using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Material;
using MiniMes.Contracts.MasterData;

namespace MiniMes.Client.Core.ViewModels.Material;

// [v2] Spec M6 step 2b, the QC "IQC" screen: deliveries waiting in quarantine, grouped by (item, supplier lot); pick
// one, enter samples and defects, Pass or Fail. One result changes every reel of that delivery. Latest results below.
public partial class IqcViewModel(IMaterialApi api, IMasterDataApi masterData, IDialogService dialogs, TimeProvider time)
    : ObservableObject, ILoadable
{
    public const string DefectCategory = "Incoming";   // Domain IqcRecord.DefectCategory (the client cannot see Domain)
    public const int RecordCount = 50;

    public ObservableCollection<PendingIqcRow> Pending { get; } = [];
    public ObservableCollection<IqcRecordRow> Records { get; } = [];
    public ObservableCollection<DefectCodeDto> DefectCodes { get; } = [];
    public FieldErrors FieldErrors { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PassCommand), nameof(FailCommand))]
    private PendingIqcRow? _selected;

    [ObservableProperty]
    private string _sampleQtyText = string.Empty;

    [ObservableProperty]
    private string _defectQtyText = "0";

    [ObservableProperty]
    private DefectCodeDto? _defectCode;

    [ObservableProperty]
    private string _note = string.Empty;

    [ObservableProperty]
    private string _resultMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            DefectCodes.Clear();
            foreach (var d in (await masterData.GetDefectCodesAsync()).Where(d => d.IsActive && d.Category == DefectCategory).OrderBy(d => d.Code))
                DefectCodes.Add(d);
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => RunAsync(ReloadAsync);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task PassAsync() => RecordAsync("Pass");

    // R17: a rejected delivery never comes back by a normal action (only an NCR decision, M7): ask first.
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task FailAsync() => RecordAsync("Fail");

    private bool HasSelection() => Selected is not null;

    partial void OnSelectedChanged(PendingIqcRow? value)
    {
        FieldErrors.Clear();
        ResultMessage = string.Empty;
    }

    private async Task RecordAsync(string result)
    {
        FieldErrors.Clear();
        ResultMessage = string.Empty;
        var row = Selected!;
        if (!TryRead(result, out var samples, out var defects)) return;
        if (result == "Fail" && !dialogs.Confirm(string.Format(Strings.Iqc_ConfirmFail, row.Item.LotCount, row.Item.ItemCode, row.Item.SupplierLot)))
            return;

        await RunAsync(async () =>
        {
            var record = await api.RecordIqcAsync(new RecordIqcRequest(row.Item.ItemId, row.Item.SupplierLot, samples, defects, result,
                DefectCode?.Code, string.IsNullOrWhiteSpace(Note) ? null : Note.Trim()));
            SampleQtyText = string.Empty;
            DefectQtyText = "0";
            DefectCode = null;
            Note = string.Empty;
            await ReloadAsync();   // the inspected delivery leaves the list, which clears the selection and its message
            ResultMessage = string.Format(result == "Pass" ? Strings.Iqc_Passed : Strings.Iqc_Failed, record.LotCount, record.ItemCode, record.SupplierLot);
        });
    }

    private bool TryRead(string result, out int samples, out int defects)
    {
        var ok = true;
        if (!int.TryParse(SampleQtyText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out samples) || samples <= 0)
        {
            FieldErrors.Set("SampleQty", Strings.Iqc_SamplesPositive);
            ok = false;
        }
        if (!int.TryParse(DefectQtyText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out defects) || (samples > 0 && defects > samples))
        {
            FieldErrors.Set("DefectQty", Strings.Iqc_DefectsRange);
            ok = false;
        }
        if (result == "Fail" && DefectCode is null) { FieldErrors.Set("DefectCode", Strings.Iqc_DefectCodeRequired); ok = false; }
        if (result == "Pass" && defects > 0 && string.IsNullOrWhiteSpace(Note)) { FieldErrors.Set("Note", Strings.Iqc_NoteRequired); ok = false; }
        return ok;
    }

    private async Task ReloadAsync()
    {
        var selected = Selected?.Item;
        Pending.Clear();
        foreach (var p in await api.GetPendingIqcAsync())
            Pending.Add(new PendingIqcRow(p, Local(p.FirstReceivedAt).ToString(ReceivingViewModel.DateTimeFormat, CultureInfo.InvariantCulture),
                p.EarliestExpiryAt is { } e ? Local(e).ToString(ReceivingViewModel.DateFormat, CultureInfo.InvariantCulture) : string.Empty));
        Selected = Pending.FirstOrDefault(r => r.Item.ItemId == selected?.ItemId && r.Item.SupplierLot == selected.SupplierLot);
        Records.Clear();
        foreach (var r in await api.GetIqcRecordsAsync(RecordCount))
            Records.Add(new IqcRecordRow(r, Local(r.InspectedAt).ToString(ReceivingViewModel.DateTimeFormat, CultureInfo.InvariantCulture)));
    }

    private DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), time.LocalTimeZone);

    // 400 under its box; 422 (another inspector was faster) on the page, with the list refreshed.
    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = string.Empty;
        try
        {
            await action();
        }
        catch (ApiException ex) when (ex.FieldErrors.Count > 0)
        {
            FieldErrors.SetAll(ex.FieldErrors);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            if (ex.Status is HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound)
            {
                try { await ReloadAsync(); } catch (ApiException) { /* the message above already says what went wrong */ }
            }
        }
    }
}

public sealed record PendingIqcRow(PendingIqcDto Item, string ReceivedText, string ExpiryText);

public sealed record IqcRecordRow(IqcRecordDto Record, string InspectedText);
