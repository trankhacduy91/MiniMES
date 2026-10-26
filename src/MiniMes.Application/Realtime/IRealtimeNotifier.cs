using MiniMes.Contracts.Realtime;

namespace MiniMes.Application.Realtime;

// Spec M4 2.6: Application only knows this interface; Infrastructure implements it with SignalR (IHubContext).
// Everything goes to the group of the machine's line, never to all clients.
public interface IRealtimeNotifier
{
    Task MachineStateChangedAsync(MachineStateChangedDto dto, CancellationToken ct = default);
    Task MachineCounterUpdatedAsync(MachineCounterDto dto, CancellationToken ct = default);
    Task MachineProcessUpdatedAsync(MachineProcessDto dto, CancellationToken ct = default);
}
