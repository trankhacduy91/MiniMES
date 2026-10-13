using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Factory;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Reads: any logged-in role (fallback policy). Writes: Admin. No DELETE: PUT IsActive=false instead.
[ApiController]
[Route("api/sites")]
public class SitesController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<SiteDto>> List(CancellationToken ct) => sender.Send(new GetSitesQuery(), ct);

    [HttpGet("{id:int}")]
    public Task<SiteDto> Get(int id, CancellationToken ct) => sender.Send(new GetSiteByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<SiteDto>> Create(CreateSiteRequest r, CancellationToken ct)
    {
        var dto = await sender.Send(new CreateSiteCommand(r.Code, r.Name), ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<SiteDto> Update(int id, UpdateSiteRequest r, CancellationToken ct)
        => sender.Send(new UpdateSiteCommand(id, r.Name, r.IsActive), ct);
}
