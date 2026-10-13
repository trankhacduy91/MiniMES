using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Catalogs;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/downtime-reasons")]
public class DowntimeReasonsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<DowntimeReasonDto>> List([FromQuery] bool? isActive, CancellationToken ct)
        => sender.Send(new GetDowntimeReasonsQuery(isActive), ct);

    [HttpGet("{id:int}")]
    public Task<DowntimeReasonDto> Get(int id, CancellationToken ct) => sender.Send(new GetDowntimeReasonByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<DowntimeReasonDto>> Create(CreateDowntimeReasonRequest r, CancellationToken ct)
    {
        var dto = await sender.Send(new CreateDowntimeReasonCommand(r.Code, r.Name, r.Category, r.CountsAgainstAvailability), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<DowntimeReasonDto> Update(int id, UpdateDowntimeReasonRequest r, CancellationToken ct)
        => sender.Send(new UpdateDowntimeReasonCommand(id, r.Name, r.Category, r.CountsAgainstAvailability), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost("{id:int}/deactivate")]
    public async Task<IActionResult> Deactivate(int id, CancellationToken ct)
    {
        await sender.Send(new DeactivateDowntimeReasonCommand(id), ct);
        return NoContent();
    }
}
