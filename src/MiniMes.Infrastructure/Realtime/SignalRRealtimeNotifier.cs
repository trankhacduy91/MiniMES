using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Andon;
using MiniMes.Contracts.Realtime;

namespace MiniMes.Infrastructure.Realtime;

// IRealtimeNotifier over SignalR, to the group of the machine's line only.
// A failed push is logged and swallowed: the dashboard is a view, it must never stop the ingest or the writer;
// the client repairs itself with the next push or the snapshot after a reconnect.
public sealed class SignalRRealtimeNotifier(IHubContext<MachineHub, IMachineClient> hub, ILogger<SignalRRealtimeNotifier> log) : IRealtimeNotifier
{
    public Task MachineStateChangedAsync(MachineStateChangedDto dto, CancellationToken ct = default)
        => SafeAsync(dto.Line, c => c.MachineStateChanged(dto), dto.Machine);

    public Task MachineCounterUpdatedAsync(MachineCounterDto dto, CancellationToken ct = default)
        => SafeAsync(dto.Line, c => c.MachineCounterUpdated(dto), dto.Machine);

    public Task MachineProcessUpdatedAsync(MachineProcessDto dto, CancellationToken ct = default)
        => SafeAsync(dto.Line, c => c.MachineProcessUpdated(dto), dto.Machine);

    public Task AndonChangedAsync(AndonDto dto, CancellationToken ct = default)
        => SafeAsync(dto.Line, c => c.AndonChanged(dto), dto.Machine);

    public async Task AndonEscalatedAsync(AndonDto dto, IReadOnlyList<string> roles, CancellationToken ct = default)
    {
        try { await hub.Clients.Groups(roles.Select(RealtimeHub.RoleGroup).ToList()).AndonEscalated(dto); }
        catch (Exception ex) { log.LogWarning(ex, "SignalR escalation push for Andon {AndonId} failed", dto.Id); }
    }

    private async Task SafeAsync(string line, Func<IMachineClient, Task> push, string machine)
    {
        try { await push(hub.Clients.Group(RealtimeHub.LineGroup(line))); }
        catch (Exception ex) { log.LogWarning(ex, "SignalR push for {Machine} failed", machine); }
    }
}
