using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.ViewModels;
using MiniMes.Client.Core.ViewModels.Kiosk;

namespace MiniMes.Kiosk.Wpf;

// Badge login -> choose the WO (home) -> scan. A fresh (Transient) ViewModel on every navigation: the next
// operator never sees the previous one's data.
public sealed partial class Navigation(IServiceProvider services) : ObservableObject, IKioskNavigation
{
    [ObservableProperty]
    private object? _currentViewModel;

    public void ShowLogin() => CurrentViewModel = services.GetRequiredService<OperatorLoginViewModel>();

    public void ShowHome() => Show(services.GetRequiredService<StationSelectViewModel>());

    // The Repair station has its own screen (check-in + repair form); every other station scans.
    public void ShowScan() => Show(services.GetRequiredService<IKioskContext>().Station?.Type == "Repair"
        ? services.GetRequiredService<RepairViewModel>()
        : services.GetRequiredService<ScanViewModel>());

    // Shown first so its "loading" text is visible; LoadAsync never throws (errors go to the screen).
    private void Show(object viewModel)
    {
        CurrentViewModel = viewModel;
        if (viewModel is ILoadable loadable) _ = loadable.LoadAsync();
    }
}
