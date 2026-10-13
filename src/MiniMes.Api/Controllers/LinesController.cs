using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Factory;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/lines")]
public class LinesController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<LineDto>> List([FromQuery] int? areaId, CancellationToken ct) => sender.Send(new GetLinesQuery(areaId), ct);

    [HttpGet("{id:int}")]
    public Task<LineDto> Get(int id, CancellationToken ct) => sender.Send(new GetLineByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<LineDto>> Create(CreateLineRequest r, CancellationToken ct)
    {
        var dto = await sender.Send(new CreateLineCommand(r.AreaId, r.Code, r.Name), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<LineDto> Update(int id, UpdateLineRequest r, CancellationToken ct)
        => sender.Send(new UpdateLineCommand(id, r.Name, r.IsActive), ct);
}
