using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Quality.Holds;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// M7 step 9: quality Hold / Release across modules (spec 2.6). Holding and releasing are QC / Supervisor decisions
// (Q6: never automatic, Q9: a reason / note and who). 422 = nothing to hold, already released, NCR closed.
[ApiController]
[Route("api/quality/holds")]
[Authorize(Roles = Quality)]
public class HoldsController(ISender sender) : ControllerBase
{
    private const string Quality = $"{nameof(Role.QC)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}";

    [HttpGet]
    public Task<IReadOnlyList<HoldBatchDto>> List([FromQuery] bool activeOnly = true, CancellationToken ct = default)
        => sender.Send(new GetHoldBatchesQuery(activeOnly), ct);

    // What a hold would take, without writing anything: the confirmation dialog shows it.
    [HttpPost("preview")]
    public Task<HoldPreviewDto> Preview(HoldTargetsRequest r, CancellationToken ct) => sender.Send(new PreviewHoldQuery(r), ct);

    [HttpPost]
    public async Task<ActionResult<HoldResultDto>> Hold(HoldTargetsRequest r, CancellationToken ct)
        => StatusCode(StatusCodes.Status201Created, await sender.Send(new HoldTargetsCommand(r), ct));

    [HttpPost("{batchId:guid}/release")]
    public Task<ReleaseResultDto> Release(Guid batchId, ReleaseHoldRequest r, CancellationToken ct)
        => sender.Send(new ReleaseHoldCommand(batchId, r.Note), ct);
}
