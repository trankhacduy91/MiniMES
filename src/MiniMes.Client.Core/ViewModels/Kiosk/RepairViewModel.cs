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

// Where the board goes after the repair: null = retest the failed step (default), or an earlier step (rework).
public sealed record ReturnOption(int? Seq, string Text);

// Spec M3 step 8: the Repair station. The technician scans the NG board (check-in, Ng -> Repair), then fills
// what was done: defect, action, replaced part, and optionally a rework step. Submit = POST /stations/{id}/repair.
public partial class RepairViewModel(IExecutionApi execution, IWorkOrderApi workOrders, IKioskContext kiosk,
    IKioskNavigation navigation, IAuthClient auth, ITokenStore tokens, IKioskFeedback feedback, TimeProvider time)
    : StationScreenViewModel(execution, kiosk, navigation, auth, tokens, feedback, time), ILoadable
{
    public ObservableCollection<DefectCodeDto> DefectCodes { get; } = [];
    public ObservableCollection<ReturnOption> ReturnOptions { get; } = [];

    // The board checked in and waiting for its repair record; null = scan a board.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormOpen))]
    [NotifyPropertyChangedFor(nameof(IsScanMode))]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private string? _board;

    public bool IsFormOpen => Board is not null;
    public bool IsScanMode => Board is null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private DefectCodeDto? _selectedDefect;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private string _action = string.Empty;

    [ObservableProperty]
    private string _componentReplaced = string.Empty;

    [ObservableProperty]
    private ReturnOption? _selectedReturn;

    [ObservableProperty]
    private string _formError = string.Empty;

    // Never throws: defect codes and the WO's steps for the rework choice.
    public async Task LoadAsync()
    {
        if (Kiosk.WorkOrder is null) return;
        try
        {
            foreach (var code in await Execution.GetDefectCodesAsync()) DefectCodes.Add(code);
            var wo = await workOrders.GetWorkOrderAsync(Kiosk.WorkOrder.WorkOrderId);
            ReturnOptions.Add(new ReturnOption(null, Strings.Repair_RetestFailedStep));
            foreach (var op in wo.Operations.OrderBy(o => o.Seq))
                ReturnOptions.Add(new ReturnOption(op.Seq, string.Format(Strings.Repair_ReworkFrom, op.Seq, op.OperationCode)));
            SelectedReturn = ReturnOptions[0];
        }
        catch (ApiException) { }
    }

    private bool CanScan() => !IsOffline && !IsFormOpen;

    protected override void OnOfflineChanged()
    {
        ScanCommand.NotifyCanExecuteChanged();
        SubmitCommand.NotifyCanExecuteChanged();
    }

    // Check-in. A board already in Repair (form abandoned, scanned again) is accepted too: the form opens again.
    [RelayCommand(CanExecute = nameof(CanScan), AllowConcurrentExecutions = true)]
    private async Task ScanAsync()
    {
        var serialNo = TakeScanInput();
        if (serialNo.Length == 0) return;
        var answer = await SendScanAsync(serialNo);
        if (answer is null) return;
        if (answer.Result != "Ok")
        {
            ShowBlocked(answer, serialNo);   // e.g. NotDefective: a good board does not belong here
            return;
        }
        Flash(ScanState.Ok, Strings.Repair_CheckedIn, Strings.Repair_FillForm, serialNo);
        Board = serialNo;
        SelectedDefect = null;
        Action = ComponentReplaced = FormError = string.Empty;
        SelectedReturn = ReturnOptions.FirstOrDefault();
    }

    private bool CanSubmit() => !IsOffline && IsFormOpen && SelectedDefect is not null && !string.IsNullOrWhiteSpace(Action);

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        if (Board is not { } board || SelectedDefect is not { } defect || Kiosk.Station is not { } station) return;
        FormError = string.Empty;
        try
        {
            var done = await Execution.RepairAsync(station.Id, new RepairRequest(board, defect.Code, Action.Trim(),
                string.IsNullOrWhiteSpace(ComponentReplaced) ? null : ComponentReplaced.Trim(), SelectedReturn?.Seq));
            Board = null;
            var next = done.NextStepName is { } name ? string.Format(Strings.Scan_Next, $"{done.NextStep} {name}") : string.Empty;
            Flash(ScanState.Ok, Strings.Repair_Done, next, board);
        }
        catch (ApiException ex) when (ex.IsNetworkError)
        {
            IsOffline = true;
            FormError = ex.Message;   // the form stays filled: submit again once online
        }
        catch (ApiException ex)
        {
            FormError = ex.Message;   // 400 (e.g. replaced part missing) or 422 (rework step after the failed one)
        }
    }

    // The board stays in Repair; scanning it again reopens the form.
    [RelayCommand]
    private void CancelRepair()
    {
        Board = null;
        FormError = string.Empty;
    }
}
