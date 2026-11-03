using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Packing;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// M3b: pallets by their label number (P261103000004).
[ApiController]
[Route("api/pallets")]
public class PalletsController(ISender sender) : ControllerBase
{
    private const string Packers = $"{nameof(Role.Operator)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}";
    private const string Readers = $"{Packers},{nameof(Role.QC)}";
    private const string Supervisors = $"{nameof(Role.Supervisor)},{nameof(Role.Admin)}";

    // The pallet with its cartons; OnHold derived from the serials inside (P10).
    [Authorize(Roles = Readers)]
    [HttpGet("{palletNo}")]
    public Task<PalletDto> Get(string palletNo, CancellationToken ct) => sender.Send(new GetPalletQuery(palletNo), ct);

    // The short last pallet; its label prints (LabelZpl). 422 = not open, empty or full.
    // At the kiosk an operator sends the supervisor's scanned badge, like the carton.
    [Authorize(Roles = Packers)]
    [HttpPost("{palletNo}/close-partial")]
    public Task<PalletDto> ClosePartial(string palletNo, ClosePartialRequest r, CancellationToken ct)
        => sender.Send(new ClosePartialPalletCommand(palletNo, r.Reason, r.SupervisorBadge), ct);

    // P8: 422 = not closed, held serials inside, already shipped; 409 = the pallet changed meanwhile.
    [Authorize(Roles = Supervisors)]
    [HttpPost("{palletNo}/ship")]
    public Task<PalletDto> Ship(string palletNo, ShipPalletRequest r, CancellationToken ct)
        => sender.Send(new ShipPalletCommand(palletNo, r.ShipmentRef), ct);

    [Authorize(Roles = Packers)]
    [HttpPost("{palletNo}/labels/preview")]
    public async Task<IActionResult> PreviewLabel(string palletNo, CancellationToken ct)
        => File(await sender.Send(new PreviewPackingLabelQuery(LabelType.Pallet, palletNo), ct), "image/png");

    // Like the carton label: closed pallets only, reprint needs a reason.
    [Authorize(Roles = Packers)]
    [HttpPost("{palletNo}/labels/print")]
    public Task<PackingLabelDto> PrintLabel(string palletNo, PrintPackingLabelRequest r, CancellationToken ct)
        => sender.Send(new PrintPackingLabelCommand(LabelType.Pallet, palletNo, r.Reason), ct);
}
