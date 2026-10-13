using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.MasterData.Import;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// [v2] Excel import of Item + BOM + Routing (spec M1 2.10). WPF only picks the file and uploads it;
// reading and checking happen here, so the rules exist in one place.
[ApiController]
[Route("api/import")]
[Authorize(Roles = nameof(Role.Admin))]
public class ImportController(ISender sender) : ControllerBase
{
    private const long MaxUploadBytes = 5 * 1024 * 1024;
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    [HttpGet("template")]
    public async Task<IActionResult> Template(CancellationToken ct)
        => File(await sender.Send(new GetImportTemplateQuery(), ct), XlsxContentType, "minimes-master-data-template.xlsx");

    // dryRun=true (default): check only. dryRun=false: check again, then write everything in one transaction.
    // Always 200: a rejected file is a normal answer with Committed=false and the full error list.
    [HttpPost("master-data")]
    [RequestSizeLimit(MaxUploadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes)]
    public async Task<ActionResult<ImportResultDto>> MasterData(IFormFile file, [FromQuery] bool dryRun = true, CancellationToken ct = default)
    {
        await using var stream = file.OpenReadStream();
        return await sender.Send(new ImportMasterDataCommand(stream, dryRun), ct);
    }
}
