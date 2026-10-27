using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using MiniMes.Contracts.Realtime;
using MiniMes.Infrastructure.Auth;

namespace MiniMes.Infrastructure.Realtime;

// Spec M4 2.6. Clients only join/leave line groups; the server pushes through IHubContext (the hub itself only lives
// for one client call). Any logged-in user may watch a line: the dashboard is read-only.
// Groups are not kept across a reconnect (new ConnectionId): the client joins again and reloads the REST snapshot.
[Authorize]
// [v2] Step 7b: on connect the server adds the connection to role:{role} for every role in the JWT, so an Andon
// escalation reaches the Admin wherever they are, without the client asking (and without being able to ask for a
// role it does not have). Also redone on every reconnect, since that is a new connection.
public sealed class MachineHub : Hub<IMachineClient>
{
    public override async Task OnConnectedAsync()
    {
        foreach (var role in Context.User?.FindAll(JwtTokenService.RoleClaim).Select(c => c.Value) ?? [])
            await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeHub.RoleGroup(role));
        await base.OnConnectedAsync();
    }

    public Task JoinLine(string lineCode) => Groups.AddToGroupAsync(Context.ConnectionId, RealtimeHub.LineGroup(lineCode));

    public Task LeaveLine(string lineCode) => Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeHub.LineGroup(lineCode));
}
