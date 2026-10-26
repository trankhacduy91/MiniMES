using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MiniMes.Contracts.Realtime;

namespace MiniMes.Infrastructure.Realtime;

// Spec M4 2.6. Clients only join/leave line groups; the server pushes through IHubContext (the hub itself only lives
// for one client call). Any logged-in user may watch a line: the dashboard is read-only.
// Groups are not kept across a reconnect (new ConnectionId): the client joins again and reloads the REST snapshot.
[Authorize]
public sealed class MachineHub : Hub<IMachineClient>
{
    public Task JoinLine(string lineCode) => Groups.AddToGroupAsync(Context.ConnectionId, RealtimeHub.LineGroup(lineCode));

    public Task LeaveLine(string lineCode) => Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeHub.LineGroup(lineCode));
}
