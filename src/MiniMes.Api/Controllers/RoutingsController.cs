using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Routings;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Same lifecycle as BomsController.
[ApiController]
[Route("api/routings")]
public class RoutingsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<RoutingSummaryDto>> List([FromQuery] int itemId, CancellationToken ct)
        => sender.Send(new GetRoutingsQuery(itemId), ct);

    [HttpGet("{id:int}")]
    public Task<RoutingDto> Get(int id, CancellationToken ct) => sender.Send(new GetRoutingByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<RoutingDto>> CreateDraft(CreateDraftRequest r, CancellationToken ct)
    {
        var routing = await sender.Send(new CreateRoutingDraftCommand(r.ItemId, r.CopyFromLatest), ct);
        return CreatedAtAction(nameof(Get), new { id = routing.Id }, routing);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}/steps")]
    public Task<RoutingDto> SaveSteps(int id, SaveRoutingStepsRequest r, CancellationToken ct)
        => sender.Send(new SaveRoutingStepsCommand(id, r.Steps), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost("{id:int}/approve")]
    public Task<RoutingDto> Approve(int id, ApproveVersionRequest r, CancellationToken ct)
        => sender.Send(new ApproveRoutingCommand(id, r.EffectiveFromUtc), ct);
}
