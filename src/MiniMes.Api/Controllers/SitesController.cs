using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Factory;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Reads: any logged-in role (fallback policy). Writes: Admin. No DELETE: PUT IsActive=false instead.
[ApiController]
[Route("api/sites")]
public class SitesController(ISiteService sites) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<SiteDto>> List(CancellationToken ct) => sites.ListAsync(ct);

    [HttpGet("{id:int}")]
    public Task<SiteDto> Get(int id, CancellationToken ct) => sites.GetAsync(id, ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<SiteDto>> Create(CreateSiteRequest request, CancellationToken ct)
    {
        var dto = await sites.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<SiteDto> Update(int id, UpdateSiteRequest request, CancellationToken ct)
        => sites.UpdateAsync(id, request, ct);
}
