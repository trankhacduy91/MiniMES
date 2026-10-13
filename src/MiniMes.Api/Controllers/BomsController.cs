using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Boms;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Lifecycle: POST (new Draft version) -> PUT lines (Draft only) -> POST approve (locks it).
// No DELETE: an approved version is retired by the next one's EffectiveFrom.
[ApiController]
[Route("api/boms")]
public class BomsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<BomSummaryDto>> List([FromQuery] int itemId, CancellationToken ct)
        => sender.Send(new GetBomsQuery(itemId), ct);

    [HttpGet("{id:int}")]
    public Task<BomDto> Get(int id, CancellationToken ct) => sender.Send(new GetBomByIdQuery(id), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<BomDto>> CreateDraft(CreateDraftRequest r, CancellationToken ct)
    {
        var bom = await sender.Send(new CreateBomDraftCommand(r.ItemId, r.CopyFromLatest), ct);
        return CreatedAtAction(nameof(Get), new { id = bom.Id }, bom);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}/lines")]
    public Task<BomDto> SaveLines(int id, SaveBomLinesRequest r, CancellationToken ct)
        => sender.Send(new SaveBomLinesCommand(id, r.Lines), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost("{id:int}/approve")]
    public Task<BomDto> Approve(int id, ApproveVersionRequest r, CancellationToken ct)
        => sender.Send(new ApproveBomCommand(id, r.EffectiveFromUtc), ct);
}
