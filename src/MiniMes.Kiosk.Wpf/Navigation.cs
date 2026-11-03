using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.ViewModels;
using MiniMes.Client.Core.ViewModels.Kiosk;
using MiniMes.Client.Core.ViewModels.Oee;

namespace MiniMes.Kiosk.Wpf;

// Badge login -> choose the WO (home) -> scan. A fresh (Transient) ViewModel on every navigation: the next
// operator never sees the previous one's data.
public sealed partial class Navigation(IServiceProvider services) : ObservableObject, IKioskNavigation
{
    [ObservableProperty]
    private object? _currentViewModel;

    public void ShowLogin() => Show(services.GetRequiredService<OperatorLoginViewModel>());

    // [v2] M5 step 8b: started with --mode=linetv, the kiosk is the line's big screen (it logs in by itself).
    public void ShowLineTv() => Show(services.GetRequiredService<LineTvViewModel>());

    public void ShowHome() => Show(services.GetRequiredService<StationSelectViewModel>());

    // The Repair station has its own screen (check-in + repair form), so has the Pack station (M3b: boards into
    // cartons, cartons onto pallets); every other station scans.
    public void ShowScan() => Show(services.GetRequiredService<IKioskContext>().Station?.Type switch
    {
        "Repair" => services.GetRequiredService<RepairViewModel>(),
        "Pack" => services.GetRequiredService<PackingViewModel>(),
        _ => (object)services.GetRequiredService<ScanViewModel>(),
    });

    public void ShowMaterial() => Show(services.GetRequiredService<LoadMaterialViewModel>());

    // Shown first so its "loading" text is visible; LoadAsync never throws (errors go to the screen).
    // The screen left behind lets go of what it holds open (the Andon panel's SignalR connection).
    private void Show(object viewModel)
    {
        if (CurrentViewModel is IClosable closable) _ = closable.CloseAsync();
        CurrentViewModel = viewModel;
        if (viewModel is ILoadable loadable) _ = loadable.LoadAsync();
    }
}
