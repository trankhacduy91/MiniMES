using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using MiniMes.Application.Audit;
using MiniMes.Contracts.Audit;
using MiniMes.Contracts.Common;
using MiniMes.Domain.Users;

namespace MiniMes.Api.Controllers;

// GET only: AuditLog is evidence, so no endpoint can change or delete it.
[ApiController]
[Route("api/audit-logs")]
[Authorize(Roles = nameof(Role.Admin))]
public class AuditLogsController(ISender sender) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AuditLogDto>> Search([FromQuery] AuditLogFilter f, CancellationToken ct)
        => sender.Send(new GetAuditLogsQuery(f.TableName, f.RecordKey, f.From, f.To, f.Page, f.PageSize), ct);
}
