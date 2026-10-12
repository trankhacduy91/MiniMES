using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.ViewModels;

namespace MiniMes.Kiosk.Wpf;

// Same idea as BackOffice, but the login screen is the badge scan.
// A fresh (Transient) ViewModel on every navigation: the next operator never sees the previous one's data.
public sealed partial class Navigation(IServiceProvider services) : ObservableObject, INavigation
{
    [ObservableProperty]
    private object? _currentViewModel;

    public void ShowLogin() => CurrentViewModel = services.GetRequiredService<OperatorLoginViewModel>();

    public void ShowHome() => CurrentViewModel = services.GetRequiredService<HomeViewModel>();
}
