using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Quality.Fai;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// [v2] M7 step 8b: first article inspection (spec 2.5b). Replaces the temporary POST /api/work-orders/{id}/fai/result
// of M3. Only QC records (Q17); Supervisors see the queue and worksheets. 409 = the WO changed since the worksheet
// was opened, 422 = cannot be judged now (no sample, not through Test, automatic check red, item not ticked).
[ApiController]
[Route("api/fai")]
public class FaiController(ISender sender) : ControllerBase
{
    private const string Readers = $"{nameof(Role.QC)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}";
    private const string Inspectors = $"{nameof(Role.QC)},{nameof(Role.Admin)}";

    [Authorize(Roles = Readers)]
    [HttpGet("queue")]
    public Task<IReadOnlyList<FaiQueueItemDto>> Queue(CancellationToken ct) => sender.Send(new GetFaiQueueQuery(), ct);

    [Authorize(Roles = Readers)]
    [HttpGet("work-orders/{woId:int}")]
    public Task<FaiWorksheetDto> Worksheet(int woId, CancellationToken ct) => sender.Send(new GetFaiWorksheetQuery(woId), ct);

    [Authorize(Roles = Inspectors)]
    [HttpPost("work-orders/{woId:int}/result")]
    public Task<FaiWorksheetDto> Record(int woId, RecordFaiRequest r, CancellationToken ct)
        => sender.Send(new RecordFaiResultCommand(woId, r.Result, r.CheckedItems ?? [], r.Note, r.DefectCode, r.RowVersion), ct);
}
