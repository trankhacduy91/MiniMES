using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Factory;
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
        var dto = await sender.Send(new CreateStationCommand(r.LineId, r.Code, r.Name, r.Type), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<StationDto> Update(int id, UpdateStationRequest r, CancellationToken ct)
        => sender.Send(new UpdateStationCommand(id, r.Name, r.Type, r.IsActive), ct);
}
