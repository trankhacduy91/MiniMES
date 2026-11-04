using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Quality.Ncrs;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// M7: nonconformance reports (spec 2.5). QC opens, reviews and proposes; a Supervisor approves (another person,
// Q11, checked by the domain); QC or a Supervisor closes. 409 = changed by someone else, 422 = not allowed now.
[ApiController]
[Route("api/ncrs")]
public class NcrsController(ISender sender) : ControllerBase
{
    private const string Quality = $"{nameof(Role.QC)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}";
    private const string Reviewers = $"{nameof(Role.QC)},{nameof(Role.Admin)}";
    private const string Approvers = $"{nameof(Role.Supervisor)},{nameof(Role.Admin)}";

    [Authorize(Roles = Quality)]
    [HttpGet]
    public Task<IReadOnlyList<NcrListItemDto>> List([FromQuery] string? status, CancellationToken ct) => sender.Send(new GetNcrsQuery(status), ct);

    [Authorize(Roles = Quality)]
    [HttpGet("{id:int}")]
    public Task<NcrDto> Get(int id, CancellationToken ct) => sender.Send(new GetNcrQuery(id), ct);

    [Authorize(Roles = Quality)]
    [HttpPost]
    public async Task<ActionResult<NcrDto>> Create(CreateNcrRequest r, CancellationToken ct)
    {
        var ncr = await sender.Send(new CreateNcrCommand(r.Source, r.SourceRef, r.DefectCode, r.Description, r.Qty, r.Targets ?? [], r.SpcAlertId), ct);
        return CreatedAtAction(nameof(Get), new { id = ncr.Id }, ncr);
    }

    [Authorize(Roles = Reviewers)]
    [HttpPost("{id:int}/start-review")]
    public Task<NcrDto> StartReview(int id, NcrTransitionRequest r, CancellationToken ct) => sender.Send(new StartNcrReviewCommand(id, r.RowVersion), ct);

    [Authorize(Roles = Reviewers)]
    [HttpPost("{id:int}/propose")]
    public Task<NcrDto> Propose(int id, ProposeDispositionRequest r, CancellationToken ct)
        => sender.Send(new ProposeDispositionCommand(id, r.Disposition, r.Note, r.ConcessionRef, r.RowVersion), ct);

    [Authorize(Roles = Approvers)]
    [HttpPost("{id:int}/approve")]
    public Task<NcrDto> Approve(int id, NcrTransitionRequest r, CancellationToken ct) => sender.Send(new ApproveNcrCommand(id, r.RowVersion), ct);

    // The approver disagrees with the proposal: wiped, QC proposes again.
    [Authorize(Roles = Approvers)]
    [HttpPost("{id:int}/reject-proposal")]
    public Task<NcrDto> RejectProposal(int id, NcrTransitionRequest r, CancellationToken ct) => sender.Send(new RejectNcrProposalCommand(id, r.RowVersion), ct);

    [Authorize(Roles = Quality)]
    [HttpPost("{id:int}/close")]
    public Task<NcrDto> Close(int id, CloseNcrRequest r, CancellationToken ct) => sender.Send(new CloseNcrCommand(id, r.CapaReference, r.RowVersion), ct);

    [Authorize(Roles = Reviewers)]
    [HttpPost("{id:int}/cancel")]
    public Task<NcrDto> Cancel(int id, CancelNcrRequest r, CancellationToken ct) => sender.Send(new CancelNcrCommand(id, r.Reason, r.RowVersion), ct);
}
