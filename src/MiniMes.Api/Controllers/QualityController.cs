using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Quality.Inspections;
using MiniMes.Application.Quality.Spc;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// M7: inspection results, SPC charts and control limits.
[ApiController]
[Route("api/quality")]
public class QualityController(ISender sender) : ControllerBase
{
    private const string Inspectors = $"{nameof(Role.Operator)},{nameof(Role.Admin)}";
    private const string ChartReaders = $"{nameof(Role.QC)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}";
    private const string QualityEngineers = $"{nameof(Role.QC)},{nameof(Role.Admin)}";

    // The values measured on a board at its inspection step, by the operator at the kiosk or a test machine account
    // (like the scan: it IS the scan of that step). Blocked is a normal answer (200 + reasonCode).
    [Authorize(Roles = Inspectors)]
    [HttpPost("inspections")]
    public Task<InspectionResponse> RecordInspection(RecordInspectionRequest r, CancellationToken ct)
        => sender.Send(new RecordInspectionCommand(r.StationId, r.SerialNo, r.Values ?? [], r.Source, r.ClientRequestId), ct);

    [Authorize(Roles = ChartReaders)]
    [HttpGet("spc/{planId:int}/chart")]
    public Task<SpcChartDto> Chart(int planId, [FromQuery] int take = 100, CancellationToken ct = default)
        => sender.Send(new GetSpcChartQuery(planId, take), ct);

    // Q3: a new baseline only on purpose, with a reason. 422 = fewer than 20 points or all equal; 409 = two at once.
    [Authorize(Roles = QualityEngineers)]
    [HttpPost("spc/{planId:int}/limits/recompute")]
    public Task<ControlLimitsDto> RecomputeLimits(int planId, RecomputeLimitsRequest r, CancellationToken ct)
        => sender.Send(new RecomputeControlLimitsCommand(planId, r.From, r.To, r.Reason), ct);
}
