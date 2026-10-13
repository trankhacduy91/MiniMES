using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Calendar;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/shifts")]
public class ShiftsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ShiftDto>> List([FromQuery] bool? isActive, CancellationToken ct)
        => sender.Send(new GetShiftsQuery(isActive), ct);

    [HttpGet("{id:int}")]
    public Task<ShiftDto> Get(int id, CancellationToken ct) => sender.Send(new GetShiftByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<ShiftDto>> Create(CreateShiftRequest r, CancellationToken ct)
    {
        var dto = await sender.Send(new CreateShiftCommand(r.Code, r.Name, r.StartTime, r.EndTime, r.Days, r.Breaks), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<ShiftDto> Update(int id, UpdateShiftRequest r, CancellationToken ct)
        => sender.Send(new UpdateShiftCommand(id, r.Name, r.StartTime, r.EndTime, r.Days, r.Breaks), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost("{id:int}/deactivate")]
    public async Task<IActionResult> Deactivate(int id, CancellationToken ct)
    {
        await sender.Send(new DeactivateShiftCommand(id), ct);
        return NoContent();
    }
}
