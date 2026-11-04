using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Quality;

namespace MiniMes.Client.Core.ViewModels.Quality;

// One line of the worksheet. Auto: green/red from MES data, read-only. Manual: QC ticks it.
public partial class FaiCheckRow(FaiCheckDto dto, Action changed) : ObservableObject
{
    public string Code { get; } = dto.Code;
    public string Text { get; } = dto.Text;
    public bool IsAuto { get; } = dto.Kind == "Auto";
    public bool IsManual => !IsAuto;
    public bool? Passed { get; } = dto.Passed;
    public string Detail { get; } = dto.Detail ?? string.Empty;
    public string StateText => IsAuto ? (Passed == true ? Strings.Fai_Green : Strings.Fai_Red) : string.Empty;

    [ObservableProperty]
    private bool _isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

// [v2] Spec M7 step 8b: the FAI screen. Left: WOs whose first article waits for QC (ready samples first). Right: the
// worksheet, automatic checks already filled from MES data (BOM trace, Test, measurements), manual ones to tick,
// earlier attempts below. Pass only with every automatic check green and every manual item ticked (Q17); the API
// checks it all again (422). Fail sends the sample to Repair and the next board becomes the sample.
public partial class FaiViewModel : ObservableObject, ILoadable
{
    private readonly IFaiApi _api;
    private readonly IMasterDataApi _master;
    private readonly IDialogService _dialogs;
    private readonly bool _isInspector;

    public FaiViewModel(IFaiApi api, IMasterDataApi master, ITokenStore tokens, IDialogService dialogs)
    {
        _api = api;
        _master = master;
        _dialogs = dialogs;
        _isInspector = (tokens.Session?.Roles ?? []).Any(r => r is "QC" or "Admin");
    }

    public ObservableCollection<FaiQueueItemDto> Queue { get; } = [];
    public ObservableCollection<FaiCheckRow> Items { get; } = [];
    public ObservableCollection<FaiRecordDto> History { get; } = [];
    public ObservableCollection<string> DefectCodes { get; } = [];

    [ObservableProperty] private FaiQueueItemDto? _selectedRow;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private string _infoMessage = string.Empty;
    [ObservableProperty] private string _note = string.Empty;
    [ObservableProperty] private string _defectCode = "FAI-FAIL";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSheet), nameof(Title), nameof(SampleText), nameof(CanFail), nameof(CanPass), nameof(PassHint))]
    [NotifyCanExecuteChangedFor(nameof(PassCommand), nameof(FailCommand))]
    private FaiWorksheetDto? _sheet;

    public bool HasSheet => Sheet is not null;
    public string Title => Sheet is { } s ? $"{s.WoNo} · {s.ItemCode} · {s.Line}" : string.Empty;
    public string SampleText => Sheet is { } s
        ? s.SampleSerialNo is { } sn
            ? string.Format(CultureInfo.CurrentCulture, Strings.Fai_Sample, sn, s.SampleStatus, s.TemplateVersion)
            : Strings.Fai_NoSample
        : string.Empty;

    public bool CanFail => _isInspector && Sheet is { SampleReady: true };
    public bool CanPass => CanFail && Sheet!.AutoChecksPassed && Items.Where(i => i.IsManual).All(i => i.IsChecked);

    // Why Pass is off, in the words QC needs: not my role, not ready (the server's reason), red check, unticked item.
    public string PassHint => Sheet is not { } s ? string.Empty
        : !_isInspector ? Strings.Fai_OnlyQc
        : s.NotReadyReason is { } why ? why
        : !s.AutoChecksPassed ? Strings.Fai_AutoRed
        : Items.Any(i => i.IsManual && !i.IsChecked) ? Strings.Fai_TickAll
        : string.Empty;

    public async Task LoadAsync()
    {
        try
        {
            foreach (var d in (await _master.GetDefectCodesAsync()).Where(d => d.IsActive).OrderBy(d => d.Code)) DefectCodes.Add(d.Code);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }
        await RefreshAsync();
    }

    partial void OnSelectedRowChanged(FaiQueueItemDto? value)
    {
        if (value is not null) _ = ShowAsync(value.WorkOrderId);
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        try
        {
            var rows = await _api.GetQueueAsync();
            Queue.Clear();
            foreach (var r in rows) Queue.Add(r);
            ErrorMessage = string.Empty;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private async Task ShowAsync(int workOrderId)
    {
        try
        {
            Show(await _api.GetWorksheetAsync(workOrderId));
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private void Show(FaiWorksheetDto sheet)
    {
        Items.Clear();
        foreach (var i in sheet.Items) Items.Add(new FaiCheckRow(i, ManualChanged));
        History.Clear();
        foreach (var h in sheet.History) History.Add(h);
        (Note, DefectCode) = (string.Empty, "FAI-FAIL");
        Sheet = sheet;
    }

    private void ManualChanged()
    {
        OnPropertyChanged(nameof(CanPass));
        OnPropertyChanged(nameof(PassHint));
        PassCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanPass))]
    private Task PassAsync() => RecordAsync("Pass", null);

    // Fail moves a real board to Repair: ask first.
    [RelayCommand(CanExecute = nameof(CanFail))]
    private Task FailAsync()
    {
        if (!_dialogs.Confirm(string.Format(CultureInfo.CurrentCulture, Strings.Fai_ConfirmFail, Sheet!.SampleSerialNo, DefectCode)))
            return Task.CompletedTask;
        return RecordAsync("Fail", DefectCode);
    }

    // 409 = the WO changed since the worksheet was opened (another QC judged, a new sample): reload and say so.
    private async Task RecordAsync(string result, string? defect)
    {
        var sheet = Sheet!;
        (ErrorMessage, InfoMessage) = (string.Empty, string.Empty);
        try
        {
            var ticked = Items.Where(i => i.IsManual && i.IsChecked).Select(i => i.Code).ToList();
            var after = await _api.RecordAsync(sheet.WorkOrderId, new RecordFaiRequest(result, ticked,
                string.IsNullOrWhiteSpace(Note) ? null : Note.Trim(), defect, sheet.RowVersion));
            InfoMessage = string.Format(CultureInfo.CurrentCulture, result == "Pass" ? Strings.Fai_Passed : Strings.Fai_Failed,
                sheet.WoNo, sheet.SampleSerialNo);
            Show(after);
            await RefreshAsync();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            if (ex.Status == HttpStatusCode.Conflict) await ShowAsync(sheet.WorkOrderId);
        }
    }
}
