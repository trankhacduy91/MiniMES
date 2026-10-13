using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Audit;
using MiniMes.Contracts.Common;

namespace MiniMes.Application.Audit;

// Read-only on purpose: there is no use case that changes or deletes AuditLog. From/To are UTC, To exclusive.
public sealed record GetAuditLogsQuery(
    string? TableName, string? RecordKey, DateTime? From, DateTime? To, int Page, int PageSize)
    : IRequest<PagedResult<AuditLogDto>>;

// Same paging rule as every other list: out of range is 400, never silently clamped.
public sealed class GetAuditLogsValidator : AbstractValidator<GetAuditLogsQuery>
{
    public GetAuditLogsValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, AuditLogFilter.MaxPageSize);
        RuleFor(x => x.To).GreaterThan(x => x.From).When(x => x.From is not null && x.To is not null)
            .WithMessage("To must be after From.");
    }
}

public sealed class GetAuditLogsHandler(IAppDbContext db) : IRequestHandler<GetAuditLogsQuery, PagedResult<AuditLogDto>>
{
    public async Task<PagedResult<AuditLogDto>> Handle(GetAuditLogsQuery q, CancellationToken ct)
    {
        var query = db.AuditLogs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.TableName)) query = query.Where(x => x.TableName == q.TableName);
        if (!string.IsNullOrWhiteSpace(q.RecordKey)) query = query.Where(x => x.RecordKey == q.RecordKey);
        if (q.From is { } from) query = query.Where(x => x.ChangedAt >= from);
        if (q.To is { } to) query = query.Where(x => x.ChangedAt < to);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(x => x.Id) // newest first
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .Select(x => new AuditLogDto(x.Id, x.TableName, x.RecordKey, x.ColumnName,
                x.OldValue, x.NewValue, x.ChangedBy, x.ChangedAt))
            .ToListAsync(ct);

        return new PagedResult<AuditLogDto>(items, q.Page, q.PageSize, total);
    }
}
