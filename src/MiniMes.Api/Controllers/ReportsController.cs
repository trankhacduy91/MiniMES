using MediatR;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Execution;
using MiniMes.Contracts.Execution;

namespace MiniMes.Api.Controllers;

// M3 step 9: production reports of one work order. Every logged-in role reads (fallback policy).
[ApiController]
[Route("api/reports")]
public class ReportsController(ISender sender) : ControllerBase
{
    [HttpGet("fpy")]
    public Task<FpyReportDto> Fpy([FromQuery] int workOrderId, CancellationToken ct) => sender.Send(new GetFpyReportQuery(workOrderId), ct);

    [HttpGet("wip")]
    public Task<WipReportDto> Wip([FromQuery] int workOrderId, CancellationToken ct) => sender.Send(new GetWipReportQuery(workOrderId), ct);
}
