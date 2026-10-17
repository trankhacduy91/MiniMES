using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Execution;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

[ApiController]
[Route("api/serials")]
public class SerialsController(ISender sender) : ControllerBase
{
    // M3: scrapping loses material and work already done, so only a Supervisor decides, with a reason.
    // Completed or already scrapped -> 422.
    [Authorize(Roles = $"{nameof(Role.Supervisor)},{nameof(Role.Admin)}")]
    [HttpPost("{serialNo}/scrap")]
    public Task<SerialDto> Scrap(string serialNo, ScrapRequest r, CancellationToken ct)
        => sender.Send(new ScrapSerialCommand(serialNo, r.Reason), ct);
}
