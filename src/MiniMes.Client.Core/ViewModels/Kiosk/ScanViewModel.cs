using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;

namespace MiniMes.Client.Core.ViewModels.Kiosk;

// Spec M3 step 6: the scan screen shell, READY on dark grey. Step 7 adds the scan box, the green/red result,
// the sounds and the offline banner.
public partial class ScanViewModel(IExecutionApi execution, IKioskContext kiosk, IKioskNavigation navigation,
    IAuthClient auth, ITokenStore tokens) : ObservableObject
{
    public string StationCode => kiosk.Station?.Code ?? string.Empty;

    public string WorkOrderText => kiosk.WorkOrder is { } wo ? string.Format(Strings.Scan_WorkOrder, wo.WoNo, wo.ItemCode) : string.Empty;

    public string OperatorText => tokens.Session is { } s ? string.Format(Strings.Kiosk_Operator, s.FullName, s.EmployeeCode) : string.Empty;

    [ObservableProperty]
    private string _statusText = Strings.Scan_Ready;

    // Another WO at this station: the next session opened replaces this one on the server.
    [RelayCommand]
    private void ChangeWorkOrder() => navigation.ShowHome();

    // End of shift: close the session so nobody scans under this operator's name, then forget the token.
    // Best effort: if the server cannot be reached, the next login at this station closes it anyway.
    [RelayCommand]
    private async Task LogoutAsync()
    {
        if (kiosk is { Station: { } station, Session: { } session })
        {
            try { await execution.CloseSessionAsync(station.Id, session.Id); }
            catch (ApiException) { }
        }
        kiosk.Clear();
        auth.Logout();
        navigation.ShowLogin();
    }
}
