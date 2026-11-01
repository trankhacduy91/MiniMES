using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Material;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Spec M6 step 2: the warehouse receives reels and prints their labels. Every logged-in role reads the lots
// (fallback policy): QC looks for what waits for IQC, supervisors for what is on the shelf.
[ApiController]
[Route("api/material/lots")]
public class MaterialLotsController(ISender sender) : ControllerBase
{
    private const string Receivers = $"{nameof(Role.Warehouse)},{nameof(Role.Admin)}";

    [HttpGet]
    public Task<PagedResult<MaterialLotDto>> List([FromQuery] MaterialLotFilter f, CancellationToken ct)
        => sender.Send(new GetMaterialLotsQuery(f.ItemId, f.Status, f.Search, f.Page, f.PageSize), ct);

    // N reels of one delivery -> N lots and their labels (ZPL) in one answer.
    [Authorize(Roles = Receivers)]
    [HttpPost]
    public async Task<ActionResult<ReceiveLotsResultDto>> Receive(ReceiveLotsRequest r, CancellationToken ct)
    {
        var result = await sender.Send(new ReceiveLotsCommand(r.ItemId, r.SupplierLot, r.QtyPerReel, r.ReelCount, r.ExpiryDate), ct);
        return StatusCode(StatusCodes.Status201Created, result);
    }

    // Reprint a torn label.
    [Authorize(Roles = Receivers)]
    [HttpGet("{id:int}/label")]
    public async Task<ContentResult> Label(int id, CancellationToken ct)
        => Content(await sender.Send(new GetLotLabelQuery(id), ct), "text/plain");

    [Authorize(Roles = Receivers)]
    [HttpGet("{id:int}/label/preview")]
    public async Task<IActionResult> PreviewLabel(int id, CancellationToken ct)
        => File(await sender.Send(new PreviewLotLabelQuery(id), ct), "image/png");
}
