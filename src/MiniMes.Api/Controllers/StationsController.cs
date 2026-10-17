using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Execution;
using MiniMes.Application.Factory;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/stations")]
public class StationsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<StationDto>> List([FromQuery] int? lineId, CancellationToken ct) => sender.Send(new GetStationsQuery(lineId), ct);

    [HttpGet("{id:int}")]
    public Task<StationDto> Get(int id, CancellationToken ct) => sender.Send(new GetStationByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<StationDto>> Create(CreateStationRequest r, CancellationToken ct)
    {
        var dto = await sender.Send(new CreateStationCommand(r.LineId, r.Code, r.Name, r.Type, r.WorkCenterId), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<StationDto> Update(int id, UpdateStationRequest r, CancellationToken ct)
        => sender.Send(new UpdateStationCommand(id, r.Name, r.Type, r.IsActive, r.WorkCenterId), ct);

    // M3: the scan of a board at this station, by an operator (kiosk) or a machine account (simulator).
    // Blocked is a normal answer (200 + reasonCode); 404 = no such active station.
    [Authorize(Roles = $"{nameof(Role.Operator)},{nameof(Role.Admin)}")]
    [HttpPost("{id:int}/scan")]
    public Task<ScanResponse> Scan(int id, ScanRequest r, CancellationToken ct)
        => sender.Send(new ScanCommand(id, r.SerialNo, r.Result, r.DefectCode, r.Source), ct);

    // M3: the technician finishes a board checked in at this Repair station (scan first). 422 = not checked in,
    // not a Repair station or a rework step that is not earlier in the routing.
    [Authorize(Roles = $"{nameof(Role.Operator)},{nameof(Role.Admin)}")]
    [HttpPost("{id:int}/repair")]
    public Task<RepairResponse> Repair(int id, RepairRequest r, CancellationToken ct)
        => sender.Send(new CompleteRepairCommand(id, r.SerialNo, r.DefectCode, r.Action, r.ComponentReplaced, r.ReturnToSeq), ct);

    // M3: the operator logs in at the station on a WO (from the line's dispatch list). Closes the station's previous
    // session. 422 = WO of another line or not on the line; 409 = another kiosk opened one at the same instant.
    [Authorize(Roles = $"{nameof(Role.Operator)},{nameof(Role.Admin)}")]
    [HttpPost("{id:int}/sessions")]
    public async Task<ActionResult<StationSessionDto>> OpenSession(int id, OpenSessionRequest r, CancellationToken ct)
    {
        var session = await sender.Send(new OpenStationSessionCommand(id, r.WorkOrderId), ct);
        return StatusCode(StatusCodes.Status201Created, session);
    }

    // M3: logout / end of shift. 422 = already closed (the next shift's login closes it too).
    [Authorize(Roles = $"{nameof(Role.Operator)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}")]
    [HttpDelete("{id:int}/sessions/{sessionId:long}")]
    public async Task<IActionResult> CloseSession(int id, long sessionId, CancellationToken ct)
    {
        await sender.Send(new CloseStationSessionCommand(id, sessionId), ct);
        return NoContent();
    }

    // M3: boards waiting at this station (next step here), FIFO by serial number. For the machine simulator (M4),
    // which logs in and opens a session like an operator first. Empty without an open session.
    [HttpGet("{id:int}/waiting-serials")]
    public Task<IReadOnlyList<WaitingSerialDto>> WaitingSerials(int id, [FromQuery] int take = 1, CancellationToken ct = default)
        => sender.Send(new GetWaitingSerialsQuery(id, take), ct);
}
