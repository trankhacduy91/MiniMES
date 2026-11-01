using MediatR;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Oee;
using MiniMes.Contracts.Oee;

namespace MiniMes.Api.Controllers;

// Spec M5 step 7: OEE of the running shift (computed on request) and of closed hours/shifts (snapshots).
// Every logged-in role reads (fallback policy): operators look at their line's OEE too.
[ApiController]
[Route("api/oee")]
public class OeeController(ISender sender) : ControllerBase
{
    // ?machine=L1-SMT-01 or ?line=L1 (exactly one).
    [HttpGet("current")]
    public Task<CurrentOeeDto> Current([FromQuery] string? machine, [FromQuery] string? line, CancellationToken ct)
        => sender.Send(new GetCurrentOeeQuery(machine, line), ct);

    // Production dates (plant time), both included; hourly=false = only the shift totals.
    [HttpGet("history")]
    public Task<IReadOnlyList<OeeSnapshotDto>> History([FromQuery] DateOnly from, [FromQuery] DateOnly to,
        [FromQuery] string? machine, [FromQuery] string? line, [FromQuery] bool hourly, CancellationToken ct)
        => sender.Send(new GetOeeHistoryQuery(from, to, machine, line, hourly), ct);
}
