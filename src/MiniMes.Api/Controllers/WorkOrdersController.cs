using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Labels;
using MiniMes.Application.Planning;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Roles (spec M2 step 7): Planner creates, releases, cancels; Supervisor holds, resumes, completes, closes;
// both change priority; QC records the first article result; Admin does everything; every logged-in role reads.
[ApiController]
[Route("api/work-orders")]
public class WorkOrdersController(ISender sender) : ControllerBase
{
    private const string Planners = $"{nameof(Role.Planner)},{nameof(Role.Admin)}";
    private const string Supervisors = $"{nameof(Role.Supervisor)},{nameof(Role.Admin)}";
    private const string Schedulers = $"{nameof(Role.Planner)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}";
    // The planner prints at release; the line supervisor reprints a damaged label on the floor.
    private const string Inspectors = $"{nameof(Role.QC)},{nameof(Role.Admin)}";
    private const string LabelPrinters = $"{nameof(Role.Planner)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}";

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

    // Snapshot + serials + Released in one transaction. Not approved BOM/routing -> 422; stale RowVersion -> 409.
    [Authorize(Roles = Planners)]
    [HttpPost("{id:int}/release")]
    public Task<WorkOrderDetailDto> Release(int id, WorkOrderTransitionRequest r, CancellationToken ct)
        => sender.Send(new ReleaseWorkOrderCommand(id, r.RowVersion), ct);

    [HttpGet("{id:int}/serials")]
    public Task<PagedResult<SerialDto>> Serials(int id, [FromQuery] SerialFilter f, CancellationToken ct)
        => sender.Send(new GetWorkOrderSerialsQuery(id, f.Status, f.Page, f.PageSize), ct);

    // One label as PNG (Labelary). Printing is a separate action with a log.
    [Authorize(Roles = LabelPrinters)]
    [HttpPost("{id:int}/labels/preview")]
    public async Task<IActionResult> PreviewLabel(int id, PreviewLabelRequest r, CancellationToken ct)
        => File(await sender.Send(new PreviewLabelQuery(id, r.SerialNo), ct), "image/png");

    // Logs every label (PrintNo; a reprint needs a reason) and returns the ZPL for the printer.
    [Authorize(Roles = LabelPrinters)]
    [HttpPost("{id:int}/labels/print")]
    public Task<PrintLabelsResultDto> PrintLabels(int id, PrintLabelsRequest r, CancellationToken ct)
        => sender.Send(new PrintLabelsCommand(id, r.SerialNos, r.Reason), ct);

    [Authorize(Roles = Supervisors)]
    [HttpPost("{id:int}/hold")]
    public Task<WorkOrderDetailDto> Hold(int id, WorkOrderTransitionRequest r, CancellationToken ct)
        => sender.Send(new HoldWorkOrderCommand(id, r.RowVersion, r.Reason), ct);

    [Authorize(Roles = Supervisors)]
    [HttpPost("{id:int}/resume")]
    public Task<WorkOrderDetailDto> Resume(int id, WorkOrderTransitionRequest r, CancellationToken ct)
        => sender.Send(new ResumeWorkOrderCommand(id, r.RowVersion), ct);

    // Reason required only below the planned good quantity at the last step.
    [Authorize(Roles = Supervisors)]
    [HttpPost("{id:int}/complete")]
    public Task<WorkOrderDetailDto> Complete(int id, WorkOrderTransitionRequest r, CancellationToken ct)
        => sender.Send(new CompleteWorkOrderCommand(id, r.RowVersion, r.Reason), ct);

    [Authorize(Roles = Supervisors)]
    [HttpPost("{id:int}/close")]
    public Task<WorkOrderDetailDto> Close(int id, WorkOrderTransitionRequest r, CancellationToken ct)
        => sender.Send(new CloseWorkOrderCommand(id, r.RowVersion), ct);

    // Only before any output; unused serials become Scrapped.
    [Authorize(Roles = Planners)]
    [HttpPost("{id:int}/cancel")]
    public Task<WorkOrderDetailDto> Cancel(int id, WorkOrderTransitionRequest r, CancellationToken ct)
        => sender.Send(new CancelWorkOrderCommand(id, r.RowVersion, r.Reason), ct);

    // [v2] Temporary (M3 step 3b): QC judges the first article; passed opens the line for the whole lot.
    // M7 replaces it with the FAI checklist use case. No sample yet / FAI not pending -> 422.
    [Authorize(Roles = Inspectors)]
    [HttpPost("{id:int}/fai/result")]
    public Task<WorkOrderDetailDto> FaiResult(int id, FaiResultRequest r, CancellationToken ct)
        => sender.Send(new RecordFaiResultCommand(id, r.Passed), ct);

    [Authorize(Roles = Schedulers)]
    [HttpPut("{id:int}/priority")]
    public Task<WorkOrderDetailDto> ChangePriority(int id, ChangePriorityRequest r, CancellationToken ct)
        => sender.Send(new ChangeWorkOrderPriorityCommand(id, r.Priority, r.RowVersion), ct);
}
