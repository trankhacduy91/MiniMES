using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Traceability;
using MiniMes.Contracts.Traceability;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// Spec M6 step 8: traceability for quality people. Unknown serial, lot or supplier lot = 200 with an empty answer.
[ApiController]
[Route("api/trace")]
[Authorize(Roles = $"{nameof(Role.QC)},{nameof(Role.Supervisor)},{nameof(Role.Admin)}")]
public class TraceController(ISender sender) : ControllerBase
{
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Either lotNo, or supplierLot + itemCode.
    [HttpGet("forward")]
    public Task<ForwardTraceResultDto> Forward(string? lotNo, string? supplierLot, string? itemCode, CancellationToken ct)
        => sender.Send(new GetForwardTraceQuery(lotNo, supplierLot, itemCode), ct);

    [HttpGet("forward.xlsx")]
    public async Task<FileContentResult> ForwardExcel(string? lotNo, string? supplierLot, string? itemCode, CancellationToken ct)
        => File(await sender.Send(new ExportForwardTraceQuery(lotNo, supplierLot, itemCode), ct), XlsxContentType,
            $"trace_{(lotNo ?? supplierLot)?.Trim()}.xlsx");

    [HttpGet("backward")]
    public Task<IReadOnlyList<BackwardTraceDto>> Backward(string serialNo, CancellationToken ct)
        => sender.Send(new GetBackwardTraceQuery(serialNo), ct);

    // Lazy tree: kind = serial (-> lots consumed) or lot (-> lots it was split from).
    [HttpGet("nodes/{kind}/{key}/children")]
    public Task<IReadOnlyList<TraceNodeDto>> Children(string kind, string key, CancellationToken ct)
        => sender.Send(new GetTraceChildrenQuery(kind, key), ct);
}
