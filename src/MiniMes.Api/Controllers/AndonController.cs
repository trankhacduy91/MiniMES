using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Andon;
using MiniMes.Contracts.Andon;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Roles (spec M4 2.9): the Operator calls for help; Supervisor and Maintenance answer (acknowledge, resolve, cancel
// a call raised by mistake); Admin does everything; every logged-in role reads.
[ApiController]
[Route("api/andon")]
public class AndonController(ISender sender) : ControllerBase
{
    private const string Callers = $"{nameof(Role.Operator)},{nameof(Role.Admin)}";
    private const string Responders = $"{nameof(Role.Supervisor)},{nameof(Role.Maintenance)},{nameof(Role.Admin)}";

    // ?status=open (dashboard) or a status name; ?line=L1.
    [HttpGet]
    public Task<IReadOnlyList<AndonDto>> List([FromQuery] string? status, [FromQuery] string? line, CancellationToken ct)
        => sender.Send(new GetAndonCallsQuery(status, line), ct);

    [HttpGet("{id:long}")]
    public Task<AndonDto> Get(long id, CancellationToken ct) => sender.Send(new GetAndonByIdQuery(id), ct);

    // 201 = new call; 200 = a call of this type was already open for the machine, that one is returned.
    [Authorize(Roles = Callers)]
    [HttpPost]
    public async Task<ActionResult<RaiseAndonResult>> Raise(RaiseAndonRequest r, CancellationToken ct)
    {
        var result = await sender.Send(new RaiseAndonCommand(r.MachineId, r.Type), ct);
        return result.Created ? CreatedAtAction(nameof(Get), new { id = result.Andon.Id }, result) : Ok(result);
    }

    // Already taken by someone else -> 409; already closed -> 422.
    [Authorize(Roles = Responders)]
    [HttpPost("{id:long}/acknowledge")]
    public Task<AndonDto> Acknowledge(long id, AndonTransitionRequest r, CancellationToken ct)
        => sender.Send(new AcknowledgeAndonCommand(id, r.RowVersion), ct);

    // Downtime reason required (400).
    [Authorize(Roles = Responders)]
    [HttpPost("{id:long}/resolve")]
    public Task<AndonDto> Resolve(long id, ResolveAndonRequest r, CancellationToken ct)
        => sender.Send(new ResolveAndonCommand(id, r.DowntimeReasonId, r.Note, r.RowVersion), ct);

    [Authorize(Roles = Responders)]
    [HttpPost("{id:long}/cancel")]
    public Task<AndonDto> Cancel(long id, AndonTransitionRequest r, CancellationToken ct)
        => sender.Send(new CancelAndonCommand(id, r.RowVersion), ct);
}
