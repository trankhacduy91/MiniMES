using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Equipment;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Master data CRUD (spec M1 step 7). Reads: any logged-in role. Writes: Admin. No DELETE: deactivate.
[ApiController]
[Route("api/work-centers")]
public class WorkCentersController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<WorkCenterDto>> List([FromQuery] int? lineId, [FromQuery] bool? isActive, CancellationToken ct)
        => sender.Send(new GetWorkCentersQuery(lineId, isActive), ct);

    [HttpGet("{id:int}")]
    public Task<WorkCenterDto> Get(int id, CancellationToken ct) => sender.Send(new GetWorkCenterByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<WorkCenterDto>> Create(CreateWorkCenterRequest r, CancellationToken ct)
    {
        var dto = await sender.Send(new CreateWorkCenterCommand(r.LineId, r.Code, r.Name, r.Type), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<WorkCenterDto> Update(int id, UpdateWorkCenterRequest r, CancellationToken ct)
        => sender.Send(new UpdateWorkCenterCommand(id, r.Name, r.Type), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost("{id:int}/deactivate")]
    public async Task<IActionResult> Deactivate(int id, CancellationToken ct)
    {
        await sender.Send(new DeactivateWorkCenterCommand(id), ct);
        return NoContent();
    }
}
