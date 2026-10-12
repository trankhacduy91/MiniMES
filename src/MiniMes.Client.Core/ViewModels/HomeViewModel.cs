using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;

namespace MiniMes.Client.Core.ViewModels;

// Placeholder main screen for M0: proves the session (who + roles) reached the client.
public partial class HomeViewModel(ITokenStore tokens, IAuthClient auth, INavigation navigation) : ObservableObject
{
    public string WelcomeText => tokens.Session is { } s
        ? string.Format(Strings.Home_Welcome, s.FullName, string.Join(", ", s.Roles))
        : string.Empty;

    public string? StationCode => tokens.Session?.StationCode;

    [RelayCommand]
    private void Logout()
    {
        auth.Logout();
        navigation.ShowLogin();
    }
}
