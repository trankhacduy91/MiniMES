using MiniMes.Contracts.Audit;
using MiniMes.Contracts.Common;

namespace MiniMes.Application.Audit;

// Read-only on purpose: there is no API to change or delete AuditLog.
public interface IAuditLogQuery
{
    Task<PagedResult<AuditLogDto>> SearchAsync(AuditLogFilter filter, CancellationToken ct);
}
