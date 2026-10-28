using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using MiniMes.Client.Core.Resources;
using MiniMes.Contracts.Machines;
using MiniMes.Contracts.Realtime;

namespace MiniMes.Client.Core.ViewModels.Machines;

// [M4b] Spec step 6: the edge gateway card next to the machine tiles. Three different failures, three looks:
//   machine Down      -> red tile, Andon (maintenance)
//   machine lost      -> grey Offline tile, this card shows "1/2 machines connected" (IT / automation)
//   gateway dead (LWT)-> this card red "OFFLINE", its machines grey after 30 s; direct MQTT machines unaffected
// A status older than the one shown (on its way while the snapshot loaded) is ignored.
public partial class GatewayCardViewModel : ObservableObject
{
    public GatewayCardViewModel(GatewayStatusDto dto)
    {
        Gateway = dto.Gateway;
        Apply(dto);
    }

    public string Gateway { get; }

    public string Title => string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_Gateway, Gateway);

    // The view colours the card with a DataTrigger on this value.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isOnline;

    [ObservableProperty]
    private string _queueText = string.Empty;

    [ObservableProperty]
    private string _devicesText = string.Empty;

    // Messages thrown away because the queue was full (rule G11): shown only when there are some.
    [ObservableProperty]
    private string _droppedText = string.Empty;

    [ObservableProperty]
    private DateTime _receivedAt;

    public string StateText => IsOnline ? Strings.Dashboard_GatewayOnline : Strings.Dashboard_GatewayOffline;

    public IReadOnlyCollection<string> Machines { get; private set; } = [];

    public bool Apply(GatewayStatusDto dto)
    {
        if (dto.Gateway != Gateway || dto.ReceivedAt < ReceivedAt) return false;
        ReceivedAt = dto.ReceivedAt;
        IsOnline = dto.State == GatewayStates.Online;
        Machines = dto.Devices.Keys.ToList();
        QueueText = dto.QueueLength == 0
            ? string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_GatewayQueue, 0)
            : string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_GatewayQueueOldest, dto.QueueLength, dto.OldestAgeSec);
        DevicesText = string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_GatewayDevices,
            dto.Devices.Count(d => d.Value == GatewayStates.Connected), dto.Devices.Count);
        DroppedText = dto.DroppedCount == 0 ? string.Empty
            : string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_GatewayDropped, dto.DroppedCount);
        return true;
    }
}
