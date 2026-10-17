using MediatR;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Execution;

namespace MiniMes.Api.Controllers;

// [v2] M3: work instructions for the kiosk. SopUrl "/sop/PCB-A/SMT-v1.pdf" is fetched as /api/sop/PCB-A/SMT-v1.pdf.
// Any logged-in user (fallback policy); read-only; 404 for unknown files and paths outside the SOP folder.
[ApiController]
[Route("api/sop")]
public class SopController(ISender sender) : ControllerBase
{
    [HttpGet("{**path}")]
    public async Task<IActionResult> Get(string path, CancellationToken ct)
    {
        var file = await sender.Send(new GetSopFileQuery(path), ct);
        return File(file.Content, file.ContentType);
    }
}
