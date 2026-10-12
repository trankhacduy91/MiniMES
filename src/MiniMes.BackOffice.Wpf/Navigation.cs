using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.ViewModels;

namespace MiniMes.BackOffice.Wpf;

// MainWindow shows whatever ViewModel is current; DataTemplates in MainWindow.xaml pick the View.
// The only place that asks the container for ViewModels, so a fresh (Transient) one is created
// on every navigation: logging out and in again never shows the previous user's data.
public sealed partial class Navigation(IServiceProvider services) : ObservableObject, INavigation
{
    [ObservableProperty]
    private object? _currentViewModel;

    public void ShowLogin() => CurrentViewModel = services.GetRequiredService<UserLoginViewModel>();

    public void ShowHome() => CurrentViewModel = services.GetRequiredService<HomeViewModel>();
}
