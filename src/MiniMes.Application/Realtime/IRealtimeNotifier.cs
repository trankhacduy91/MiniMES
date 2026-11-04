using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Oee;
using MiniMes.Contracts.Quality;
using MiniMes.Contracts.Realtime;

namespace MiniMes.Application.Realtime;

// Spec M4 2.6: Application only knows this interface; Infrastructure implements it with SignalR (IHubContext).
// Everything goes to the group of the machine's line, never to all clients; [v2] escalations go to role groups.
public interface IRealtimeNotifier
{
    Task MachineStateChangedAsync(MachineStateChangedDto dto, CancellationToken ct = default);
    Task MachineCounterUpdatedAsync(MachineCounterDto dto, CancellationToken ct = default);
    Task MachineProcessUpdatedAsync(MachineProcessDto dto, CancellationToken ct = default);
    Task AndonChangedAsync(AndonDto dto, CancellationToken ct = default);
    Task AndonEscalatedAsync(AndonDto dto, IReadOnlyList<string> roles, CancellationToken ct = default);   // [v2] role groups
    Task GatewayStatusChangedAsync(GatewayStatusDto dto, CancellationToken ct = default);                  // [M4b] everyone
    Task DowntimeNeedsClassificationAsync(UnclassifiedDowntimeDto dto, CancellationToken ct = default);   // [M5] line group
    Task OeeSnapshotClosedAsync(OeeSnapshotClosedDto dto, CancellationToken ct = default);                // [M5] line group
    Task SpcAlertRaisedAsync(SpcAlertDto dto, CancellationToken ct = default);                            // [M7] role groups
    Task FaiStatusChangedAsync(FaiStatusChangedDto dto, CancellationToken ct = default);                  // [M7] line group
}
