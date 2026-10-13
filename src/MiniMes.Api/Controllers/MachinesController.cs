using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Equipment;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/machines")]
public class MachinesController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<MachineDto>> List([FromQuery] int? workCenterId, [FromQuery] bool? isActive, CancellationToken ct)
        => sender.Send(new GetMachinesQuery(workCenterId, isActive), ct);

    [HttpGet("{id:int}")]
    public Task<MachineDto> Get(int id, CancellationToken ct) => sender.Send(new GetMachineByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<MachineDto>> Create(CreateMachineRequest r, CancellationToken ct)
    {
        var dto = await sender.Send(new CreateMachineCommand(r.WorkCenterId, r.Code, r.Name, r.Type, r.MqttTopicKey, r.DataSource), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<MachineDto> Update(int id, UpdateMachineRequest r, CancellationToken ct)
        => sender.Send(new UpdateMachineCommand(id, r.Name, r.Type, r.DataSource), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost("{id:int}/deactivate")]
    public async Task<IActionResult> Deactivate(int id, CancellationToken ct)
    {
        await sender.Send(new DeactivateMachineCommand(id), ct);
        return NoContent();
    }
}
