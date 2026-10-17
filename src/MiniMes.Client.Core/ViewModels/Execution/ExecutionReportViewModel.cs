using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;

namespace MiniMes.Client.Core.ViewModels.Execution;

public sealed record FpyRow(FpyStepDto Step)
{
    public string Fpy => ExecutionReportViewModel.Pct(Step.FpyPct);
}

// Spec M3 step 9: FPY per step (+ RTY, final yield) and WIP per position for one work order (BackOffice).
public partial class ExecutionReportViewModel(IReportApi reports, IWorkOrderApi workOrders) : ObservableObject, ILoadable
{
    // Orders that have (or had) boards on the line; Draft and Cancelled have nothing to report.
    private static readonly string[] Reportable = ["Released", "InProgress", "OnHold", "Completed", "Closed"];

    public ObservableCollection<WorkOrderSummaryDto> WorkOrders { get; } = [];
    public ObservableCollection<FpyRow> FpySteps { get; } = [];
    public ObservableCollection<WipRowDto> WipRows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private WorkOrderSummaryDto? _selectedWorkOrder;

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string _wipTotal = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public static string Pct(decimal? value) => value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) + " %" : "-";

    public async Task LoadAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            var page = await workOrders.GetWorkOrdersAsync(null, null, null, 1, 200);
            WorkOrders.Clear();
            foreach (var wo in page.Items.Where(w => Reportable.Contains(w.Status))) WorkOrders.Add(wo);
            SelectedWorkOrder = WorkOrders.FirstOrDefault();   // loads its reports (OnSelectedWorkOrderChanged)
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    partial void OnSelectedWorkOrderChanged(WorkOrderSummaryDto? value) => _ = RefreshAsync();

    private bool CanRefresh() => SelectedWorkOrder is not null;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (SelectedWorkOrder is not { } wo) return;
        ErrorMessage = string.Empty;
        try
        {
            var fpy = await reports.GetFpyAsync(wo.Id);
            var wip = await reports.GetWipAsync(wo.Id);
            if (SelectedWorkOrder?.Id != wo.Id) return;   // the user picked another order meanwhile
            FpySteps.Clear();
            foreach (var step in fpy.Steps) FpySteps.Add(new FpyRow(step));
            WipRows.Clear();
            foreach (var row in wip.Rows.OrderBy(r => r.CurrentSeq).ThenBy(r => r.Status)) WipRows.Add(row);
            Summary = string.Format(Strings.Report_Summary, Pct(fpy.RtyPct), Pct(fpy.FinalYieldPct), fpy.Completed, fpy.Scrapped);
            WipTotal = string.Format(Strings.Report_WipTotal, wip.Total);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
