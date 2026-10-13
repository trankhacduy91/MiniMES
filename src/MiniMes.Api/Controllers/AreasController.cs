using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Factory;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/areas")]
public class AreasController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AreaDto>> List([FromQuery] int? siteId, CancellationToken ct) => sender.Send(new GetAreasQuery(siteId), ct);

    [HttpGet("{id:int}")]
    public Task<AreaDto> Get(int id, CancellationToken ct) => sender.Send(new GetAreaByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<AreaDto>> Create(CreateAreaRequest r, CancellationToken ct)
    {
        var dto = await sender.Send(new CreateAreaCommand(r.SiteId, r.Code, r.Name), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<AreaDto> Update(int id, UpdateAreaRequest r, CancellationToken ct)
        => sender.Send(new UpdateAreaCommand(id, r.Name, r.IsActive), ct);
}
