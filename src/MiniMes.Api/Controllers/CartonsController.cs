using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Packing;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// M3b: cartons by their label number (C261103000123).
[ApiController]
[Route("api/cartons")]
public class CartonsController(ISender sender) : ControllerBase
{
    private const string Packers = $"{nameof(Role.Operator)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}";
    private const string Readers = $"{Packers},{nameof(Role.QC)}";
    private const string Supervisors = $"{nameof(Role.Supervisor)},{nameof(Role.Admin)}";

    // The carton with its live serials; OnHold derived from them (P10).
    [Authorize(Roles = Readers)]
    [HttpGet("{cartonNo}")]
    public Task<CartonDto> Get(string cartonNo, CancellationToken ct) => sender.Send(new GetCartonQuery(cartonNo), ct);

    // P6: the short last carton of a WO; its label prints (LabelZpl). 422 = not open, empty or full; 409 = a pack in between.
    // At the kiosk an operator sends the supervisor's scanned badge (400 when missing or not a supervisor).
    [Authorize(Roles = Packers)]
    [HttpPost("{cartonNo}/close-partial")]
    public Task<CartonDto> ClosePartial(string cartonNo, ClosePartialRequest r, CancellationToken ct)
        => sender.Send(new ClosePartialCartonCommand(cartonNo, r.Reason, r.SupervisorBadge), ct);

    // P9: one serial out, history kept; a closed carton opens again to be filled up. 422 = on a pallet, shipped, the
    // serial is not in it, or the station has another open carton of the WO.
    [Authorize(Roles = Supervisors)]
    [HttpPost("{cartonNo}/unpack")]
    public Task<CartonDto> Unpack(string cartonNo, UnpackRequest r, CancellationToken ct)
        => sender.Send(new UnpackSerialCommand(cartonNo, r.SerialNo, r.Reason), ct);

    // Needed before unpacking a carton that is on a pallet. 422 = shipped, not on a pallet.
    [Authorize(Roles = Supervisors)]
    [HttpPost("{cartonNo}/remove-from-pallet")]
    public Task<CartonDto> RemoveFromPallet(string cartonNo, CancellationToken ct)
        => sender.Send(new RemoveCartonFromPalletCommand(cartonNo), ct);

    // The label as PNG (Labelary), no log. An open carton shows its quantity so far.
    [Authorize(Roles = Packers)]
    [HttpPost("{cartonNo}/labels/preview")]
    public async Task<IActionResult> PreviewLabel(string cartonNo, CancellationToken ct)
        => File(await sender.Send(new PreviewPackingLabelQuery(LabelType.Carton, cartonNo), ct), "image/png");

    // Print (again) the label of a closed carton: logged, PrintNo + 1, a reason when printed before (P11).
    // 404 = no such carton; 422 = still open, or a reprint without a reason; 409 = two reprints at the same instant.
    [Authorize(Roles = Packers)]
    [HttpPost("{cartonNo}/labels/print")]
    public Task<PackingLabelDto> PrintLabel(string cartonNo, PrintPackingLabelRequest r, CancellationToken ct)
        => sender.Send(new PrintPackingLabelCommand(LabelType.Carton, cartonNo, r.Reason), ct);
}
