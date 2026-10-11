using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Factory;
using MiniMes.Contracts.Factory;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/stations")]
public class StationsController(IStationService stations) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<StationDto>> List([FromQuery] int? lineId, CancellationToken ct) => stations.ListAsync(lineId, ct);

    [HttpGet("{id:int}")]
    public Task<StationDto> Get(int id, CancellationToken ct) => stations.GetAsync(id, ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<StationDto>> Create(CreateStationRequest request, CancellationToken ct)
    {
        var dto = await stations.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<StationDto> Update(int id, UpdateStationRequest request, CancellationToken ct)
        => stations.UpdateAsync(id, request, ct);
}
