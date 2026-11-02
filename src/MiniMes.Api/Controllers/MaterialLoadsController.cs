using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Material;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Spec M6 step 4: reels on a station. The verdict of a load is a normal answer (200 + outcome), like a scan;
// 404 = no such station, 422 = no WO open at the station, 409 = another kiosk loaded the same item at that instant.
[ApiController]
[Route("api/stations/{stationId:int}/material-loads")]
public class MaterialLoadsController(ISender sender) : ControllerBase
{
    private const string Loaders = $"{nameof(Role.Operator)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}";

    // The checklist: what the open WO needs at this station and which lot is on it.
    [HttpGet]
    public Task<IReadOnlyList<StationMaterialDto>> Checklist(int stationId, CancellationToken ct)
        => sender.Send(new GetStationMaterialsQuery(stationId), ct);

    [Authorize(Roles = Loaders)]
    [HttpPost]
    public Task<LoadMaterialResponse> Load(int stationId, LoadMaterialRequest r, CancellationToken ct)
        => sender.Send(new LoadMaterialCommand(stationId, r.LotNo, r.SlotNo, r.SupervisorBadge, r.OverrideReason), ct);

    [Authorize(Roles = Loaders)]
    [HttpPost("{loadId:int}/unload")]
    public Task<IReadOnlyList<StationMaterialDto>> Unload(int stationId, int loadId, UnloadMaterialRequest r, CancellationToken ct)
        => sender.Send(new UnloadMaterialCommand(stationId, loadId, r.RemainingQty), ct);
}
