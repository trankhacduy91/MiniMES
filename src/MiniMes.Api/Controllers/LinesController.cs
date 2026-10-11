using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Factory;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/lines")]
public class LinesController(ILineService lines) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<LineDto>> List([FromQuery] int? areaId, CancellationToken ct) => lines.ListAsync(areaId, ct);

    [HttpGet("{id:int}")]
    public Task<LineDto> Get(int id, CancellationToken ct) => lines.GetAsync(id, ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<LineDto>> Create(CreateLineRequest request, CancellationToken ct)
    {
        var dto = await lines.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<LineDto> Update(int id, UpdateLineRequest request, CancellationToken ct)
        => lines.UpdateAsync(id, request, ct);
}
