using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Planning;

namespace MiniMes.Client.Core.ViewModels.Planning;

// Work order detail (spec M2 step 8): header, materials, operations with progress, serials (paged), and the
// status buttons. A button is enabled only when the server lists its action in allowedActions: the screen
// never repeats the state machine (spec 2.6). Every change sends the RowVersion this screen loaded.
public partial class WorkOrderDetailViewModel(IWorkOrderApi api, IPageNavigator pages, IDialogService dialogs, TimeProvider time)
    : ObservableObject
{
    public const int SerialPageSize = 100;
    public const string ZplFilter = "ZPL label files (*.zpl)|*.zpl";

    public ObservableCollection<WorkOrderMaterialDto> Materials { get; } = [];
    public ObservableCollection<WorkOrderOperationDto> Operations { get; } = [];
    public ObservableCollection<SerialDto> Serials { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(PlannedWindow), nameof(Versions), nameof(Progress), nameof(Reason))]
    [NotifyCanExecuteChangedFor(nameof(ReleaseCommand), nameof(HoldCommand), nameof(ResumeCommand), nameof(CompleteCommand),
        nameof(CloseCommand), nameof(CancelCommand), nameof(PreviewLabelCommand), nameof(PrintLabelsCommand), nameof(ReprintSelectedCommand))]
    private WorkOrderDetailDto? _detail;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReprintSelectedCommand))]
    private SerialDto? _selectedSerial;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SerialPageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousSerialsCommand), nameof(NextSerialsCommand))]
    private int _serialPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SerialPageText))]
    [NotifyCanExecuteChangedFor(nameof(NextSerialsCommand))]
    private int _serialTotal;

    [ObservableProperty]
    private byte[]? _labelImage;

    [ObservableProperty]
    private string _infoMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public string Title => Detail is null ? string.Empty
        : string.Format(Strings.WorkOrderDetail_Title, Detail.WoNo, Detail.ItemCode, Detail.Status);

    public string PlannedWindow => Detail is null ? string.Empty
        : $"{PlantTime.Format(Detail.PlannedStart, time)} - {PlantTime.Format(Detail.PlannedEnd, time)}";

    public string Versions => Detail?.BomVersion is { } bom ? $"BOM v{bom} / RT v{Detail.RoutingVersion}" : Strings.WorkOrderDetail_NotReleased;

    public string Progress => Detail is null ? string.Empty
        : string.Format(Strings.WorkOrderDetail_Progress, Detail.QtyGoodLastStep, Detail.PlannedQty, Detail.SerialCount);

    // Why it is on hold / completed short / cancelled: the last reason that applies.
    public string Reason => Detail?.HoldReason ?? Detail?.CompleteReason ?? Detail?.CancelReason ?? string.Empty;

    private int SerialPageCount => Math.Max(1, (SerialTotal + SerialPageSize - 1) / SerialPageSize);

    public string SerialPageText => string.Format(Strings.Common_PageInfo, SerialPage, SerialPageCount, SerialTotal);

    public Task InitAsync(int id) => RunAsync(async () =>
    {
        Show(await api.GetWorkOrderAsync(id));
        await LoadSerialsAsync();
    });

    // ---------- status buttons ----------

    [RelayCommand(CanExecute = nameof(CanRelease))]
    private Task ReleaseAsync()
        => dialogs.Confirm(string.Format(Strings.WorkOrderDetail_ConfirmRelease, Detail!.WoNo, Detail.PlannedQty))
            ? TransitionAsync("release", null) : Task.CompletedTask;

    private bool CanRelease() => Allows("Release");

    [RelayCommand(CanExecute = nameof(CanHold))]
    private Task HoldAsync() => AskThen(Strings.WorkOrderDetail_HoldPrompt, reason => TransitionAsync("hold", reason));

    private bool CanHold() => Allows("Hold");

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => TransitionAsync("resume", null);

    private bool CanResume() => Allows("Resume");

    // Below the planned good quantity a reason is required (rule R10), so ask for it; otherwise just confirm.
    [RelayCommand(CanExecute = nameof(CanComplete))]
    private Task CompleteAsync()
    {
        var d = Detail!;
        if (d.QtyGoodLastStep < d.PlannedQty)
            return AskThen(string.Format(Strings.WorkOrderDetail_CompleteShortPrompt, d.QtyGoodLastStep, d.PlannedQty),
                reason => TransitionAsync("complete", reason));
        return dialogs.Confirm(string.Format(Strings.WorkOrderDetail_ConfirmComplete, d.WoNo))
            ? TransitionAsync("complete", null) : Task.CompletedTask;
    }

    private bool CanComplete() => Allows("Complete");

    [RelayCommand(CanExecute = nameof(CanClose))]
    private Task CloseAsync()
        => dialogs.Confirm(string.Format(Strings.WorkOrderDetail_ConfirmClose, Detail!.WoNo))
            ? TransitionAsync("close", null) : Task.CompletedTask;

    private bool CanClose() => Allows("Close");

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private Task CancelAsync() => AskThen(Strings.WorkOrderDetail_CancelPrompt, reason => TransitionAsync("cancel", reason));

    private bool CanCancel() => Allows("Cancel");

    // ---------- labels ----------

    // One label as PNG; the selected serial, or the first one.
    [RelayCommand(CanExecute = nameof(CanPrint))]
    private Task PreviewLabelAsync() => RunAsync(async () =>
        LabelImage = await api.PreviewLabelAsync(Detail!.Id, new PreviewLabelRequest(SelectedSerial?.SerialNo)));

    private bool CanPrint() => Allows("PrintLabels");

    // Every label of the order, saved as a .zpl file for the printer; the server logs each print.
    [RelayCommand(CanExecute = nameof(CanPrint))]
    private Task PrintLabelsAsync() => PrintAsync(serialNos: null, reason: null);

    // A damaged label: reprint one serial, with the reason the print log requires.
    [RelayCommand(CanExecute = nameof(CanReprint))]
    private Task ReprintSelectedAsync()
        => AskThen(string.Format(Strings.WorkOrderDetail_ReprintPrompt, SelectedSerial!.SerialNo),
            reason => PrintAsync([SelectedSerial.SerialNo], reason));

    private bool CanReprint() => CanPrint() && SelectedSerial is not null;

    private async Task PrintAsync(IReadOnlyList<string>? serialNos, string? reason)
    {
        var d = Detail!;
        var file = serialNos is null ? $"{d.WoNo}-labels.zpl" : $"{d.WoNo}-{serialNos[0]}.zpl";
        if (dialogs.PickFileToSave(file, ZplFilter) is not { } path) return;
        await RunAsync(async () =>
        {
            var result = await api.PrintLabelsAsync(d.Id, new PrintLabelsRequest(serialNos, reason));
            await File.WriteAllTextAsync(path, result.Zpl);
            InfoMessage = string.Format(Strings.WorkOrderDetail_Printed, result.Printed, result.Reprinted, path);
        });
    }

    // ---------- serials, navigation ----------

    [RelayCommand(CanExecute = nameof(CanGoBackSerials))]
    private Task PreviousSerialsAsync()
    {
        SerialPage--;
        return RunAsync(LoadSerialsAsync);
    }

    private bool CanGoBackSerials() => SerialPage > 1;

    [RelayCommand(CanExecute = nameof(CanGoForwardSerials))]
    private Task NextSerialsAsync()
    {
        SerialPage++;
        return RunAsync(LoadSerialsAsync);
    }

    private bool CanGoForwardSerials() => SerialPage < SerialPageCount;

    [RelayCommand]
    private Task RefreshAsync() => Detail is null ? Task.CompletedTask : InitAsync(Detail.Id);

    [RelayCommand]
    private Task BackAsync() => pages.ShowAsync<WorkOrderListViewModel>();

    // ---------- helpers ----------

    private bool Allows(string action) => Detail?.AllowedActions.Contains(action) == true;

    private Task AskThen(string prompt, Func<string, Task> then)
        => dialogs.AskText(Detail!.WoNo, prompt) is { } reason ? then(reason) : Task.CompletedTask;

    private Task TransitionAsync(string action, string? reason) => RunAsync(async () =>
    {
        Show(await api.TransitionAsync(Detail!.Id, action, new WorkOrderTransitionRequest(Detail.RowVersion, reason)));
        await LoadSerialsAsync();   // release creates them, cancel scraps them
    });

    private void Show(WorkOrderDetailDto detail)
    {
        Detail = detail;
        Materials.Clear();
        foreach (var m in detail.Materials) Materials.Add(m);
        Operations.Clear();
        foreach (var o in detail.Operations) Operations.Add(o);
    }

    private async Task ReloadQuietlyAsync()
    {
        try
        {
            Show(await api.GetWorkOrderAsync(Detail!.Id));
            await LoadSerialsAsync();
        }
        catch (ApiException) { /* the caller's message matters more than a failed reload */ }
    }

    private async Task LoadSerialsAsync()
    {
        var page = await api.GetSerialsAsync(Detail!.Id, SerialPage, SerialPageSize);
        Serials.Clear();
        foreach (var s in page.Items) Serials.Add(s);
        SerialTotal = page.TotalCount;
    }

    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
        try
        {
            await action();
        }
        // Someone changed the order since this screen loaded it: show the current state, then say why.
        // 409 = both saves overlapped; 422 = the other change was already committed, so the action no longer
        // fits the status (e.g. a second Release). Either way this screen was stale, so reload it.
        catch (ApiException ex) when (ex.Status is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity && Detail is not null)
        {
            await ReloadQuietlyAsync();
            ErrorMessage = ex.Status == HttpStatusCode.Conflict ? Strings.WorkOrder_Conflict : ex.Message;
        }
        catch (ApiException ex) when (ex.FieldErrors.Count > 0)   // e.g. an empty reason
        {
            ErrorMessage = string.Join(" ", ex.FieldErrors.Values.Select(v => v[0]));
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}

// Times are stored in UTC and shown in plant time (the PC's time zone: the screens run in the plant).
public static class PlantTime
{
    public const string Pattern = "yyyy-MM-dd HH:mm";

    public static string Format(DateTime utc, TimeProvider time)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), time.LocalTimeZone).ToString(Pattern);
}
