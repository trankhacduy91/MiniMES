using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;

namespace MiniMes.Client.Core.ViewModels;

// Kiosk login: the badge reader "types" the employee code followed by Enter.
// The station comes from this PC's appsettings, never from the operator.
public partial class OperatorLoginViewModel(IAuthClient auth, INavigation navigation, IOptions<ClientOptions> options)
    : ObservableObject
{
    public string StationCode { get; } = options.Value.StationCode ?? string.Empty;

    public string StationLabel => string.Format(Strings.Kiosk_Station, StationCode);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginCommand))]
    private string _employeeCode = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    private bool CanLogin() => !string.IsNullOrWhiteSpace(EmployeeCode) && !string.IsNullOrWhiteSpace(StationCode);

    [RelayCommand(CanExecute = nameof(CanLogin))]
    private async Task LoginAsync()
    {
        ErrorMessage = string.Empty;
        try
        {
            await auth.LoginOperatorAsync(EmployeeCode.Trim(), StationCode);
            navigation.ShowHome();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Status switch
            {
                HttpStatusCode.Unauthorized => Strings.Kiosk_UnknownBadge,
                HttpStatusCode.NotFound => string.Format(Strings.Kiosk_StationNotFound, StationCode),
                _ => ex.Message,
            };
        }
        finally
        {
            // Empty box = ready for the next badge scan without touching the keyboard.
            EmployeeCode = string.Empty;
        }
    }
}
