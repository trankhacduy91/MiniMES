using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Audit;
using MiniMes.Contracts.Audit;
using MiniMes.Contracts.Common;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Audit;

public sealed class AuditLogQuery(AppDbContext db) : IAuditLogQuery
{
    public async Task<PagedResult<AuditLogDto>> SearchAsync(AuditLogFilter filter, CancellationToken ct)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, AuditLogFilter.MaxPageSize);

        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.TableName)) query = query.Where(x => x.TableName == filter.TableName);
        if (!string.IsNullOrWhiteSpace(filter.RecordKey)) query = query.Where(x => x.RecordKey == filter.RecordKey);
        if (filter.From is { } from) query = query.Where(x => x.ChangedAt >= from);
        if (filter.To is { } to) query = query.Where(x => x.ChangedAt < to);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.Id) // newest first
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new AuditLogDto(x.Id, x.TableName, x.RecordKey, x.ColumnName,
                x.OldValue, x.NewValue, x.ChangedBy, x.ChangedAt))
            .ToListAsync(ct);

        return new PagedResult<AuditLogDto>(items, page, pageSize, total);
    }
}
