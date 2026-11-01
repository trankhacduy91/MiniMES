using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Common;
using MiniMes.Domain.Material;

namespace MiniMes.Application.Material;

// ---------- GET /api/material/iqc/pending: what QC still has to inspect ----------

public sealed record GetPendingIqcQuery : IRequest<IReadOnlyList<PendingIqcDto>>;

// Grouped by (item, supplier lot): QC inspects a delivery, not each reel (spec 2.4b screen).
public sealed class GetPendingIqcHandler(IAppDbContext db) : IRequestHandler<GetPendingIqcQuery, IReadOnlyList<PendingIqcDto>>
{
    public async Task<IReadOnlyList<PendingIqcDto>> Handle(GetPendingIqcQuery q, CancellationToken ct)
    {
        var groups = await db.MaterialLots.AsNoTracking()
            .Where(l => l.Status == MaterialLotStatus.PendingIqc && l.SupplierLot != null)
            .GroupBy(l => new { l.ItemId, l.SupplierLot })
            .Select(g => new
            {
                g.Key.ItemId, g.Key.SupplierLot, Count = g.Count(), Qty = g.Sum(l => l.Qty),
                First = g.Min(l => l.ReceivedAt), Expiry = g.Min(l => l.ExpiryAt),
            })
            .ToListAsync(ct);
        var itemIds = groups.Select(g => g.ItemId).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        return groups.OrderBy(g => g.First)   // oldest delivery first: it has waited longest in quarantine
            .Select(g => new PendingIqcDto(g.ItemId, items[g.ItemId].Code, items[g.ItemId].Name, items[g.ItemId].Uom,
                g.SupplierLot!, g.Count, g.Qty, g.First, g.Expiry))
            .ToList();
    }
}

// ---------- POST /api/material/iqc: one result for a whole supplier lot ----------

public sealed record RecordIqcCommand(int ItemId, string SupplierLot, int SampleQty, int DefectQty, string Result,
    string? DefectCode, string? Note) : IRequest<IqcRecordDto>;

public sealed class RecordIqcValidator : AbstractValidator<RecordIqcCommand>
{
    public RecordIqcValidator()
    {
        RuleFor(x => x.SupplierLot).NotEmpty().MaximumLength(MaterialLot.SupplierLotMaxLength);
        RuleFor(x => x.Result).IsEnumName(typeof(IqcResult))
            .WithMessage($"Result must be one of: {string.Join(", ", Enum.GetNames<IqcResult>())}.");
        RuleFor(x => x.SampleQty).GreaterThan(0).LessThanOrEqualTo(100_000);
        RuleFor(x => x.DefectQty).GreaterThanOrEqualTo(0).LessThanOrEqualTo(x => x.SampleQty)
            .WithMessage("Defects must be between 0 and the number of samples.");
        RuleFor(x => x.DefectCode).NotEmpty().When(x => x.Result == nameof(IqcResult.Fail))
            .WithMessage("A failed IQC needs a defect code.");
        // Defects found but the lot passes: allowed by the AQL plan (accept up to c defects), the note says so.
        RuleFor(x => x.Note).NotEmpty().When(x => x.Result == nameof(IqcResult.Pass) && x.DefectQty > 0)
            .WithMessage("Passing with defects needs a note (e.g. within AQL 0.65, accept 1).");
        RuleFor(x => x.Note).MaximumLength(IqcRecord.NoteMaxLength);
    }
}

// Spec 2.4b / R16: in ONE transaction, every reel of (item, supplier lot) still PendingIqc changes status with one
// set-based UPDATE, and one IqcRecord says who inspected how many samples. A second QC pressing at the same time
// updates 0 rows and gets a clear 422, without a second record. Reels received later for the same supplier lot stay
// pending: a new delivery needs a new inspection.
public sealed class RecordIqcHandler(IAppDbContext db, ICurrentUser user, TimeProvider time)
    : IRequestHandler<RecordIqcCommand, IqcRecordDto>
{
    public async Task<IqcRecordDto> Handle(RecordIqcCommand cmd, CancellationToken ct)
    {
        var result = Enum.Parse<IqcResult>(cmd.Result);
        var supplierLot = cmd.SupplierLot.Trim();
        var item = await db.Items.AsNoTracking().SingleOrDefaultAsync(i => i.Id == cmd.ItemId, ct)
                   ?? throw new KeyNotFoundException($"Item {cmd.ItemId} not found.");
        string? defectCode = null;
        if (!string.IsNullOrWhiteSpace(cmd.DefectCode))
        {
            defectCode = cmd.DefectCode.Trim();
            var code = defectCode;
            if (!await db.DefectCodes.AnyAsync(d => d.Code == code && d.IsActive && d.Category == IqcRecord.DefectCategory, ct))
                throw new ValidationException([new FluentValidation.Results.ValidationFailure(nameof(cmd.DefectCode),
                    $"'{code}' is not an active {IqcRecord.DefectCategory} defect code.")]);
        }

        var newStatus = result == IqcResult.Pass ? MaterialLotStatus.Available : MaterialLotStatus.IqcRejected;
        await using var tx = await db.BeginTransactionAsync(ct);
        // ExecuteUpdate skips the change tracker and the audit interceptor: the IqcRecord below is the audit of it.
        var affected = await db.MaterialLots
            .Where(l => l.ItemId == item.Id && l.SupplierLot == supplierLot && l.Status == MaterialLotStatus.PendingIqc)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Status, newStatus), ct);
        if (affected == 0)
        {
            if (!await db.MaterialLots.AnyAsync(l => l.ItemId == item.Id && l.SupplierLot == supplierLot, ct))
                throw new KeyNotFoundException($"No lots of '{item.Code}' with supplier lot '{supplierLot}'.");
            throw new DomainException($"No lots of '{item.Code}' / '{supplierLot}' are pending IQC (already inspected?).");
        }

        var by = user.EmployeeCode ?? throw new InvalidOperationException("IQC needs a logged-in user.");
        var record = new IqcRecord(item.Id, supplierLot, affected, cmd.SampleQty, cmd.DefectQty, result, defectCode, cmd.Note,
            by, time.GetUtcNow().UtcDateTime);
        db.IqcRecords.Add(record);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return record.ToDto(item.Code);
    }
}

// ---------- GET /api/material/iqc/records: the latest results ----------

public sealed record GetIqcRecordsQuery(int Take = 50) : IRequest<IReadOnlyList<IqcRecordDto>>;

public sealed class GetIqcRecordsValidator : AbstractValidator<GetIqcRecordsQuery>
{
    public GetIqcRecordsValidator() => RuleFor(x => x.Take).InclusiveBetween(1, 500);
}

public sealed class GetIqcRecordsHandler(IAppDbContext db) : IRequestHandler<GetIqcRecordsQuery, IReadOnlyList<IqcRecordDto>>
{
    public async Task<IReadOnlyList<IqcRecordDto>> Handle(GetIqcRecordsQuery q, CancellationToken ct)
        => await (from r in db.IqcRecords.AsNoTracking()
                  join i in db.Items on r.ItemId equals i.Id
                  orderby r.InspectedAt descending, r.Id descending
                  select new IqcRecordDto(r.Id, r.ItemId, i.Code, r.SupplierLot, r.LotCount, r.SampleQty, r.DefectQty,
                      r.Result.ToString(), r.DefectCode, r.Note, r.InspectedBy, r.InspectedAt))
            .Take(q.Take).ToListAsync(ct);
}

internal static class IqcMapping
{
    public static IqcRecordDto ToDto(this IqcRecord r, string itemCode) => new(r.Id, r.ItemId, itemCode, r.SupplierLot, r.LotCount,
        r.SampleQty, r.DefectQty, r.Result.ToString(), r.DefectCode, r.Note, r.InspectedBy, r.InspectedAt);
}
