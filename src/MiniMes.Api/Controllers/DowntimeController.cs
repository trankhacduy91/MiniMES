using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Oee.Downtime;
using MiniMes.Contracts.Oee;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Spec M5 steps 6-7: the stop Pareto and the "to classify" list (every logged-in role reads); only a Supervisor
// (or Admin) says why a machine stopped, and that change is audited.
[ApiController]
[Route("api/downtime")]
public class DowntimeController(ISender sender) : ControllerBase
{
    private const string Classifiers = $"{nameof(Role.Supervisor)},{nameof(Role.Admin)}";

    // Production dates, both included, at most 31 days; shift = one shift code; by=minutes|count;
    // includePlanned adds PM, meetings...
    [HttpGet("pareto")]
    public Task<ParetoDto> Pareto([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] string? machine,
        [FromQuery] string? line, [FromQuery] string? shift, [FromQuery] string by = ParetoBy.Minutes,
        [FromQuery] bool includePlanned = false, CancellationToken ct = default)
        => sender.Send(new GetDowntimeParetoQuery(from, to, machine, line, by, includePlanned, shift), ct);

    // from/to: UTC instants; default the last 7 days.
    [HttpGet("unclassified")]
    public Task<IReadOnlyList<UnclassifiedDowntimeDto>> Unclassified([FromQuery] int? machineId, [FromQuery] DateTime? from,
        [FromQuery] DateTime? to, CancellationToken ct)
        => sender.Send(new GetUnclassifiedDowntimeQuery(machineId, from, to), ct);

    // OTHER without a note -> 400; the stop still going on -> 422.
    [Authorize(Roles = Classifiers)]
    [HttpPut("{id:long}/reason")]
    public Task<DowntimeSegmentDto> AssignReason(long id, AssignDowntimeReasonRequest r, CancellationToken ct)
        => sender.Send(new AssignDowntimeReasonCommand(id, r.DowntimeReasonId, r.Note), ct);
}
