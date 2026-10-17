using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Planning;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Reads: any logged-in role. Create/edit/preview: Planner (and Admin), spec M2 step 7.
[ApiController]
[Route("api/work-orders")]
public class WorkOrdersController(ISender sender) : ControllerBase
{
    private const string Planners = $"{nameof(Role.Planner)},{nameof(Role.Admin)}";

    [HttpGet]
    public Task<PagedResult<WorkOrderSummaryDto>> List([FromQuery] WorkOrderFilter f, CancellationToken ct)
        => sender.Send(new GetWorkOrdersQuery(f.Status, f.LineId, f.Search, f.Page, f.PageSize), ct);

    [HttpGet("{id:int}")]
    public Task<WorkOrderDetailDto> Get(int id, CancellationToken ct) => sender.Send(new GetWorkOrderByIdQuery(id), ct);

    // Materials and steps Release would freeze, from the master data in effect now. Saves nothing.
    [Authorize(Roles = Planners)]
    [HttpPost("preview")]
    public Task<WorkOrderPreviewDto> Preview(PreviewWorkOrderRequest r, CancellationToken ct)
        => sender.Send(new PreviewWorkOrderQuery(r.ItemId, r.PlannedQty), ct);

    [Authorize(Roles = Planners)]
    [HttpPost]
    public async Task<ActionResult<WorkOrderDetailDto>> Create(CreateWorkOrderRequest r, CancellationToken ct)
    {
        var wo = await sender.Send(new CreateWorkOrderCommand(r.ItemId, r.LineId, r.PlannedQty, r.PlannedStart, r.PlannedEnd,
            r.Priority, r.RequireFai), ct);
        return CreatedAtAction(nameof(Get), new { id = wo.Id }, wo);
    }

    // Draft only (422 otherwise); a stale RowVersion -> 409.
    [Authorize(Roles = Planners)]
    [HttpPut("{id:int}")]
    public Task<WorkOrderDetailDto> Update(int id, UpdateWorkOrderRequest r, CancellationToken ct)
        => sender.Send(new UpdateWorkOrderCommand(id, r.LineId, r.PlannedQty, r.PlannedStart, r.PlannedEnd, r.RequireFai,
            r.RowVersion), ct);
}
