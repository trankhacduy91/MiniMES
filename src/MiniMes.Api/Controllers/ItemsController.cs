using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Boms;
using MiniMes.Application.MasterData.Items;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Thin controller: HTTP -> command/query -> ISender.Send -> HTTP. Rules live in validators and Domain.
// Reads: any logged-in role (fallback policy). Writes: Admin (spec M1 1.2.11).
[ApiController]
[Route("api/items")]
public class ItemsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<ItemDto>> List([FromQuery] ItemFilter f, CancellationToken ct)
        => sender.Send(new GetItemsQuery(f.Search, f.Type, f.IsActive, f.Page, f.PageSize), ct);

    [HttpGet("{id:int}")]
    public Task<ItemDto> Get(int id, CancellationToken ct) => sender.Send(new GetItemByIdQuery(id), ct);

    // Total material need for qty units, every level (one recursive CTE). Semi without a BOM -> 422.
    [HttpGet("{id:int}/explosion")]
    public Task<BomExplosionDto> Explosion(int id, [FromQuery] ExplosionFilter f, CancellationToken ct)
        => sender.Send(new GetBomExplosionQuery(id, f.Qty, f.AsOfUtc), ct);

    // The same explosion kept as a tree, for the BOM TreeView.
    [HttpGet("{id:int}/bom-tree")]
    public Task<BomTreeNodeDto> BomTree(int id, [FromQuery] ExplosionFilter f, CancellationToken ct)
        => sender.Send(new GetBomTreeQuery(id, f.Qty, f.AsOfUtc), ct);

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost]
    public async Task<ActionResult<ItemDto>> Create(CreateItemRequest r, CancellationToken ct)
    {
        var item = await sender.Send(
            new CreateItemCommand(r.Code, r.Name, r.Type, r.Uom, r.IsSerialized, r.IsLotControlled, r.SkipIqc), ct);
        return CreatedAtAction(nameof(Get), new { id = item.Id }, item);
    }

    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPut("{id:int}")]
    public Task<ItemDto> Update(int id, UpdateItemRequest r, CancellationToken ct)
        => sender.Send(new UpdateItemCommand(id, r.Name, r.SkipIqc), ct);

    // No DELETE: deactivate instead (history must keep the item).
    [Authorize(Roles = nameof(Role.Admin))]
    [HttpPost("{id:int}/deactivate")]
    public async Task<IActionResult> Deactivate(int id, CancellationToken ct)
    {
        await sender.Send(new DeactivateItemCommand(id), ct);
        return NoContent();
    }
}
