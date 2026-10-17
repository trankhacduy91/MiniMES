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

// One row of the list: planned start in plant time, progress as "good at last step / planned".
public sealed record WorkOrderRow(WorkOrderSummaryDto Wo, string PlannedStartText, string Progress)
{
    public bool IsOpen => Wo.Status is not ("Completed" or "Closed" or "Cancelled");
}

public sealed record LineOption(int? Id, string Code);

// Work order list (spec M2 step 8). Filter by status and line on the server, sorted like the dispatch list
// (priority, then planned start); FAI column; set the priority of the selected order.
public partial class WorkOrderListViewModel(IWorkOrderApi api, IMasterDataApi masterData, IPageNavigator pages, TimeProvider time)
    : ObservableObject, ILoadable
{
    public const int PageSize = 50;

    public ObservableCollection<WorkOrderRow> Rows { get; } = [];
    public ObservableCollection<LineOption> LineOptions { get; } = [];

    public IReadOnlyList<string> StatusOptions { get; } =
        [Strings.Common_All, "Draft", "Released", "InProgress", "OnHold", "Completed", "Closed", "Cancelled"];

    public IReadOnlyList<int> PriorityOptions { get; } = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    [ObservableProperty]
    private string _statusFilter = Strings.Common_All;

    [ObservableProperty]
    private LineOption? _lineFilter;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetPriorityCommand), nameof(OpenCommand))]
    private WorkOrderRow? _selected;

    [ObservableProperty]
    private int _newPriority = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    private int _page = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    private int _totalCount;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    private int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);

    public string PageText => string.Format(Strings.Common_PageInfo, Page, PageCount, TotalCount);

    public async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            var lines = await masterData.GetLinesAsync();
            LineOptions.Clear();
            LineOptions.Add(new LineOption(null, Strings.Common_All));
            foreach (var l in lines.Where(l => l.IsActive).OrderBy(l => l.Code)) LineOptions.Add(new LineOption(l.Id, l.Code));
            LineFilter = LineOptions[0];
        });
        await ReloadAsync();
    }

    [RelayCommand]
    private Task SearchAsync()
    {
        Page = 1;
        return ReloadAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PreviousPageAsync()
    {
        Page--;
        return ReloadAsync();
    }

    private bool CanGoBack() => Page > 1;

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task NextPageAsync()
    {
        Page++;
        return ReloadAsync();
    }

    private bool CanGoForward() => Page < PageCount;

    [RelayCommand]
    private Task NewAsync() => pages.ShowAsync<WorkOrderCreateViewModel>(vm => vm.InitAsync());

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task OpenAsync() => pages.ShowAsync<WorkOrderDetailViewModel>(vm => vm.InitAsync(Selected!.Wo.Id));

    private bool HasSelection() => Selected is not null;

    // [v2] Reordering the queue is a daily supervisor task (rule R15). The list has no RowVersion, so read the
    // order first; a change by someone else between the two calls still ends as a 409 message, not a crash.
    [RelayCommand(CanExecute = nameof(CanSetPriority))]
    private Task SetPriorityAsync() => RunAsync(async () =>
    {
        var id = Selected!.Wo.Id;
        var current = await api.GetWorkOrderAsync(id);
        await api.ChangePriorityAsync(id, new ChangePriorityRequest(NewPriority, current.RowVersion));
        await ReloadAsync();
    });

    private bool CanSetPriority() => Selected is { IsOpen: true };

    private Task ReloadAsync() => RunAsync(async () =>
    {
        var status = StatusFilter == Strings.Common_All ? null : StatusFilter;
        var search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        var result = await api.GetWorkOrdersAsync(status, LineFilter?.Id, search, Page, PageSize);
        Rows.Clear();
        foreach (var wo in result.Items)
            Rows.Add(new WorkOrderRow(wo, PlantTime.Format(wo.PlannedStart, time), $"{wo.QtyGoodLastStep} / {wo.PlannedQty}"));
        TotalCount = result.TotalCount;
    });

    private async Task RunAsync(Func<Task> action)
    {
        ErrorMessage = string.Empty;
        try
        {
            await action();
        }
        catch (ApiException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            await ReloadAsync();                          // show the order as it is now...
            ErrorMessage = Strings.WorkOrder_Conflict;    // ...then say why (the reload clears the message)
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}
