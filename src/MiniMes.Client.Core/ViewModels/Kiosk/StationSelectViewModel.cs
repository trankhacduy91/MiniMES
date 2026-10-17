using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using MiniMes.Client.Core.Api;
using MiniMes.Client.Core.Auth;
using MiniMes.Client.Core.Http;
using MiniMes.Client.Core.Kiosk;
using MiniMes.Client.Core.Navigation;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Planning;

namespace MiniMes.Client.Core.ViewModels.Kiosk;

// One row of the dispatch list as the operator reads it: "good at last step / planned".
public sealed record DispatchRow(DispatchListItemDto Wo)
{
    public string Progress => $"{Wo.QtyGoodLastStep} / {Wo.PlannedQty}";
    public string Fai => Wo.FaiStatus ?? "-";
}

// Spec M3 step 6: after the badge login, the operator picks the WO to run at this kiosk's station.
// The station is fixed by appsettings (never chosen); the list is the line's dispatch list (M2), most urgent
// first and pre-selected, so a normal shift start is one tap.
public partial class StationSelectViewModel(IExecutionApi execution, IWorkOrderApi workOrders, IKioskContext kiosk,
    IKioskNavigation navigation, IAuthClient auth, ITokenStore tokens, IOptions<ClientOptions> options)
    : ObservableObject, ILoadable
{
    public string StationCode { get; } = options.Value.StationCode ?? string.Empty;

    public string StationLabel => string.Format(Strings.Kiosk_Station, StationCode);

    public string OperatorText => tokens.Session is { } s ? string.Format(Strings.Kiosk_Operator, s.FullName, s.EmployeeCode) : string.Empty;

    public ObservableCollection<DispatchRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DispatchRow? _selected;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    // Never throws: the navigation starts it right after the screen is shown.
    public async Task LoadAsync()
    {
        ErrorMessage = string.Empty;
        StatusMessage = Strings.StationSelect_Loading;
        Rows.Clear();
        try
        {
            var station = (await execution.GetStationsAsync()).FirstOrDefault(s => s.Code == StationCode);
            if (station is null)
            {
                StatusMessage = string.Empty;
                ErrorMessage = string.Format(Strings.Kiosk_StationNotFound, StationCode);
                return;
            }
            kiosk.SetStation(station);
            foreach (var wo in await workOrders.GetDispatchListAsync(station.LineId)) Rows.Add(new DispatchRow(wo));
            Selected = Rows.FirstOrDefault();
            StatusMessage = Rows.Count == 0 ? Strings.StationSelect_NoWorkOrders : string.Empty;
        }
        catch (ApiException ex)
        {
            StatusMessage = string.Empty;
            ErrorMessage = ex.Message;
        }
    }

    private bool CanStart() => Selected is not null;

    // Opens the station session (the server closes the previous shift's one), then the scan screen.
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (Selected is not { } row || kiosk.Station is not { } station) return;
        ErrorMessage = string.Empty;
        try
        {
            var session = await execution.OpenSessionAsync(station.Id, row.Wo.WorkOrderId);
            kiosk.Start(session, row.Wo);
            navigation.ShowScan();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;   // e.g. 422: the WO left the line meanwhile -> Refresh
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void Logout()
    {
        kiosk.Clear();
        auth.Logout();
        navigation.ShowLogin();
    }
}
