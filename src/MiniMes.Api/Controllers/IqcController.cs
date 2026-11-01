using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Material;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// [v2] Spec M6 step 2b: incoming inspection. QC records one result per supplier lot; every role reads what waits.
[ApiController]
[Route("api/material/iqc")]
public class IqcController(ISender sender) : ControllerBase
{
    private const string Inspectors = $"{nameof(Role.QC)},{nameof(Role.Admin)}";

    [HttpGet("pending")]
    public Task<IReadOnlyList<PendingIqcDto>> Pending(CancellationToken ct) => sender.Send(new GetPendingIqcQuery(), ct);

    [HttpGet("records")]
    public Task<IReadOnlyList<IqcRecordDto>> Records([FromQuery] int take = 50, CancellationToken ct = default)
        => sender.Send(new GetIqcRecordsQuery(take), ct);

    // 404: no such supplier lot; 422: nothing pending any more (inspected by someone else a moment ago).
    [Authorize(Roles = Inspectors)]
    [HttpPost]
    public Task<IqcRecordDto> Record(RecordIqcRequest r, CancellationToken ct)
        => sender.Send(new RecordIqcCommand(r.ItemId, r.SupplierLot, r.SampleQty, r.DefectQty, r.Result, r.DefectCode, r.Note), ct);
}
