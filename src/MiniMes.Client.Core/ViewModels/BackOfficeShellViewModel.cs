using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Client.Core.ViewModels.MasterData;

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
