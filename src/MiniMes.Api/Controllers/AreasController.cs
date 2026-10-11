using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Factory;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/areas")]
public class AreasController(IAreaService areas) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<AreaDto>> List([FromQuery] int? siteId, CancellationToken ct) => areas.ListAsync(siteId, ct);

    [HttpGet("{id:int}")]
    public Task<AreaDto> Get(int id, CancellationToken ct) => areas.GetAsync(id, ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<AreaDto>> Create(CreateAreaRequest request, CancellationToken ct)
    {
        var dto = await areas.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<AreaDto> Update(int id, UpdateAreaRequest request, CancellationToken ct)
        => areas.UpdateAsync(id, request, ct);
}
