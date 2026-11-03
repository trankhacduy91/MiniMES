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
