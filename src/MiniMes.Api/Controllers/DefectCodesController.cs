using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Catalogs;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/defect-codes")]
public class DefectCodesController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<DefectCodeDto>> List([FromQuery] bool? isActive, CancellationToken ct)
        => sender.Send(new GetDefectCodesQuery(isActive), ct);

    [HttpGet("{id:int}")]
    public Task<DefectCodeDto> Get(int id, CancellationToken ct) => sender.Send(new GetDefectCodeByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<DefectCodeDto>> Create(CreateDefectCodeRequest r, CancellationToken ct)
    {
        var dto = await sender.Send(new CreateDefectCodeCommand(r.Code, r.Name, r.Category), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<DefectCodeDto> Update(int id, UpdateDefectCodeRequest r, CancellationToken ct)
        => sender.Send(new UpdateDefectCodeCommand(id, r.Name, r.Category), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost("{id:int}/deactivate")]
    public async Task<IActionResult> Deactivate(int id, CancellationToken ct)
    {
        await sender.Send(new DeactivateDefectCodeCommand(id), ct);
        return NoContent();
    }
}
