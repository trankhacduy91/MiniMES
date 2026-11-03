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
