using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Quality;

namespace MiniMes.Application.Quality.Ncrs;

internal static class NcrReads
{
    // The detail with its targets by code and their status now, the active holds (Q12) and the alert it came from.
    public static async Task<NcrDto> GetDtoAsync(this IAppDbContext db, int id, CancellationToken ct)
    {
        var ncr = await db.Ncrs.AsNoTracking().Include(n => n.Items).SingleOrDefaultAsync(n => n.Id == id, ct)
                  ?? throw new KeyNotFoundException($"NCR {id} not found.");
        var serialIds = ncr.Items.Where(i => i.TargetType == HoldTargetType.Serial).Select(i => i.TargetId).ToList();
        var lotIds = ncr.Items.Where(i => i.TargetType == HoldTargetType.Lot).Select(i => (int)i.TargetId).ToList();
        var serials = await db.Serials.AsNoTracking().Where(s => serialIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, s => (s.SerialNo, Status: s.Status.ToString()), ct);
        var lots = await db.MaterialLots.AsNoTracking().Where(l => lotIds.Contains(l.Id))
            .ToDictionaryAsync(l => (long)l.Id, l => (l.LotNo, Status: l.Status.ToString()), ct);
        var defect = ncr.DefectCodeId is { } d ? await db.DefectCodes.Where(x => x.Id == d).Select(x => x.Code).SingleAsync(ct) : null;
        var holds = await db.HoldRecords.CountAsync(h => h.NcrId == id && h.ReleasedAt == null, ct);
        var alert = await db.SpcAlerts.Where(a => a.NcrId == id).Select(a => (long?)a.Id).FirstOrDefaultAsync(ct);

        var targets = ncr.Items.OrderBy(i => i.TargetType).ThenBy(i => i.Id).Select(i =>
        {
            var (code, status) = i.TargetType == HoldTargetType.Serial ? serials[i.TargetId] : lots[i.TargetId];
            return new NcrTargetDto(i.TargetType.ToString(), i.TargetId, code, status);
        }).ToList();
        return new NcrDto(ncr.Id, ncr.NcrNo, ncr.Status.ToString(), ncr.Source.ToString(), ncr.SourceRef, defect, ncr.Description,
            ncr.Qty, ncr.Disposition?.ToString(), ncr.DispositionNote, ncr.ConcessionRef, ncr.CapaReference, ncr.CreatedBy,
            ncr.CreatedAt, ncr.ReviewedBy, ncr.ProposedBy, ncr.ProposedAt, ncr.ApprovedBy, ncr.ApprovedAt, ncr.ClosedBy,
            ncr.ClosedAt, ncr.CancelReason, targets, holds, alert, Convert.ToBase64String(ncr.RowVersion));
    }
}

// The NCR list, newest first; status null = every status.
public sealed record GetNcrsQuery(string? Status) : IRequest<IReadOnlyList<NcrListItemDto>>;

public sealed class GetNcrsHandler(IAppDbContext db) : IRequestHandler<GetNcrsQuery, IReadOnlyList<NcrListItemDto>>
{
    public const int MaxRows = 500;

    public async Task<IReadOnlyList<NcrListItemDto>> Handle(GetNcrsQuery q, CancellationToken ct)
    {
        var query = db.Ncrs.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q.Status))
        {
            if (!Enum.TryParse<NcrStatus>(q.Status, ignoreCase: true, out var status))
                throw new FluentValidation.ValidationException($"Unknown NCR status '{q.Status}'.");
            query = query.Where(n => n.Status == status);   // index (Status, CreatedAt)
        }
        var rows = await (from n in query
                          join d in db.DefectCodes on n.DefectCodeId equals d.Id into dj
                          from d in dj.DefaultIfEmpty()
                          orderby n.CreatedAt descending, n.Id descending
                          select new { n.Id, n.NcrNo, n.Status, n.Source, Defect = d.Code, n.Description, n.Qty,
                              Targets = n.Items.Count, n.Disposition, n.CreatedBy, n.CreatedAt })
            .Take(MaxRows).ToListAsync(ct);
        return rows.Select(r => new NcrListItemDto(r.Id, r.NcrNo, r.Status.ToString(), r.Source.ToString(), r.Defect, r.Description,
            r.Qty, r.Targets, r.Disposition?.ToString(), r.CreatedBy, r.CreatedAt)).ToList();
    }
}

public sealed record GetNcrQuery(int Id) : IRequest<NcrDto>;

public sealed class GetNcrHandler(IAppDbContext db) : IRequestHandler<GetNcrQuery, NcrDto>
{
    public Task<NcrDto> Handle(GetNcrQuery q, CancellationToken ct) => db.GetDtoAsync(q.Id, ct);
}
