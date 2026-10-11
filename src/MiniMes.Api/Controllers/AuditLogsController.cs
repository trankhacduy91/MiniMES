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
public class AuditLogsController(IAuditLogQuery query) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<AuditLogDto>> Search([FromQuery] AuditLogFilter filter, CancellationToken ct)
        => query.SearchAsync(filter, ct);
}
