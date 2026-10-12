using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;

namespace MiniMes.Client.Core.ViewModels;

// BackOffice login: employee code + password.
public partial class UserLoginViewModel(IAuthClient auth, INavigation navigation) : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _employeeCode = string.Empty;

    // WPF's PasswordBox cannot be data-bound (by design), so the View's code-behind pushes the
    // value here on PasswordChanged. Never shown, never logged; cleared after a successful login.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    private bool CanLogin() => !string.IsNullOrWhiteSpace(EmployeeCode) && !string.IsNullOrEmpty(Password);

    // Async command: disabled while running, so a double click does not send two logins.
    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            await auth.LoginAsync(EmployeeCode.Trim(), Password);
            Password = string.Empty;
            navigation.ShowHome();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Status == HttpStatusCode.Unauthorized ? Strings.Login_InvalidCredentials : ex.Message;
        }
    }
}
