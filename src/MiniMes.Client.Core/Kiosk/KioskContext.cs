using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Factory;
using MiniMes.Contracts.Planning;

namespace MiniMes.Client.Core.Kiosk;

// What the kiosk is doing right now: its station (from appsettings), the open session and its WO.
// Singleton state shared by the screens (ViewModels stay Transient); cleared at logout so the next
// operator starts from nothing.
public interface IKioskContext
{
    StationDto? Station { get; }
    StationSessionDto? Session { get; }
    DispatchListItemDto? WorkOrder { get; }
    void SetStation(StationDto station);
    void Start(StationSessionDto session, DispatchListItemDto workOrder);
    void Clear();
}

public sealed class KioskContext : IKioskContext
{
    public StationDto? Station { get; private set; }
    public StationSessionDto? Session { get; private set; }
    public DispatchListItemDto? WorkOrder { get; private set; }

    public void SetStation(StationDto station) => Station = station;

    public void Start(StationSessionDto session, DispatchListItemDto workOrder)
    {
        Session = session;
        WorkOrder = workOrder;
    }

    public void Clear()
    {
        Station = null;
        Session = null;
        WorkOrder = null;
    }
}
