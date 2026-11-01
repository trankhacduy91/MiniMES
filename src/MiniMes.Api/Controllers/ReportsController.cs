using MediatR;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Execution;
using MiniMes.Application.Oee;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Oee;

namespace MiniMes.Api.Controllers;

// M3 step 9: production reports of one work order; [v2] M5 step 7: OEE and downtime Pareto as Excel files.
// Every logged-in role reads (fallback policy).
[ApiController]
[Route("api/reports")]
public class ReportsController(ISender sender) : ControllerBase
{
    [HttpGet("fpy")]
    public Task<FpyReportDto> Fpy([FromQuery] int workOrderId, CancellationToken ct) => sender.Send(new GetFpyReportQuery(workOrderId), ct);

    [HttpGet("wip")]
    public Task<WipReportDto> Wip([FromQuery] int workOrderId, CancellationToken ct) => sender.Send(new GetWipReportQuery(workOrderId), ct);

    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // Shift totals per machine, production dates from..to (at most 31 days).
    [HttpGet("oee.xlsx")]
    public async Task<FileContentResult> OeeExcel([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] string? line, CancellationToken ct)
        => File(await sender.Send(new ExportOeeReportQuery(from, to, line), ct), XlsxContentType, $"oee_{from:yyyyMMdd}_{to:yyyyMMdd}.xlsx");

    [HttpGet("downtime-pareto.xlsx")]
    public async Task<FileContentResult> ParetoExcel([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] string? line,
        [FromQuery] string by = ParetoBy.Minutes, CancellationToken ct = default)
        => File(await sender.Send(new ExportParetoReportQuery(from, to, line, by), ct), XlsxContentType,
            $"downtime-pareto_{from:yyyyMMdd}_{to:yyyyMMdd}.xlsx");
}
