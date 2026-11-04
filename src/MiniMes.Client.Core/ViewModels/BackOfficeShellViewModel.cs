using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.Execution;
using MiniMes.Client.Core.ViewModels.Machines;
using MiniMes.Client.Core.ViewModels.Material;
using MiniMes.Client.Core.ViewModels.Oee;
using MiniMes.Client.Core.ViewModels.MasterData;
using MiniMes.Client.Core.ViewModels.Planning;
using MiniMes.Client.Core.ViewModels.Quality;
using MiniMes.Client.Core.ViewModels.Traceability;

namespace MiniMes.Client.Core.ViewModels;

// BackOffice main screen after login: menu on the left, the current page on the right.
public partial class BackOfficeShellViewModel(ITokenStore tokens, IAuthClient auth, INavigation navigation, IPageNavigator pages)
    : ObservableObject
{
    public IPageNavigator Pages => pages;

    public string WelcomeText => tokens.Session is { } s
        ? string.Format(Strings.Home_Welcome, s.FullName, string.Join(", ", s.Roles))
        : string.Empty;

    // Called once the shell is on screen: the item list is the landing page.
    public Task StartAsync() => ShowItemsAsync();

    [RelayCommand]
    private Task ShowWorkOrdersAsync() => pages.ShowAsync<WorkOrderListViewModel>();

    [RelayCommand]
    private Task ShowReportsAsync() => pages.ShowAsync<ExecutionReportViewModel>();

    [RelayCommand]
    private Task ShowLineDashboardAsync() => pages.ShowAsync<LineDashboardViewModel>();

    [RelayCommand]
    private Task ShowOeeDashboardAsync() => pages.ShowAsync<OeeDashboardViewModel>();

    [RelayCommand]
    private Task ShowReceivingAsync() => pages.ShowAsync<ReceivingViewModel>();

    [RelayCommand]
    private Task ShowIqcAsync() => pages.ShowAsync<IqcViewModel>();

    [RelayCommand]
    private Task ShowTraceAsync() => pages.ShowAsync<TraceViewModel>();

    [RelayCommand]
    private Task ShowSpcChartAsync() => pages.ShowAsync<ControlChartViewModel>();   // M7

    [RelayCommand]
    private Task ShowNcrsAsync() => pages.ShowAsync<NcrViewModel>();   // M7 step 8

    [RelayCommand]
    private Task ShowItemsAsync() => pages.ShowAsync<ItemListViewModel>();

    [RelayCommand]
    private Task ShowBomsAsync() => pages.ShowAsync<BomViewModel>();

    [RelayCommand]
    private Task ShowRoutingsAsync() => pages.ShowAsync<RoutingEditorViewModel>();

    [RelayCommand]
    private Task ShowWorkCentersAsync() => pages.ShowAsync<WorkCentersViewModel>();

    [RelayCommand]
    private Task ShowMachinesAsync() => pages.ShowAsync<MachinesViewModel>();

    [RelayCommand]
    private Task ShowShiftsAsync() => pages.ShowAsync<ShiftsViewModel>();

    [RelayCommand]
    private Task ShowPlantCalendarAsync() => pages.ShowAsync<PlantCalendarViewModel>();

    [RelayCommand]
    private Task ShowDefectCodesAsync() => pages.ShowAsync<DefectCodesViewModel>();

    [RelayCommand]
    private Task ShowDowntimeReasonsAsync() => pages.ShowAsync<DowntimeReasonsViewModel>();

    [RelayCommand]
    private Task ShowImportAsync() => pages.ShowAsync<ImportViewModel>();

    [RelayCommand]
    private void Logout()
    {
        pages.Clear(); // the next user must not see this user's last page
        auth.Logout();
        navigation.ShowLogin();
    }
}
