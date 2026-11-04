using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.Planning;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Common;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;

namespace MiniMes.Application.Quality.Holds;

// ---------- Preview: "this lot went into 87 boards ... Hold all?" (QC, Supervisor, Admin) ----------

public sealed record PreviewHoldQuery(HoldTargetsRequest Request) : IRequest<HoldPreviewDto>;

public sealed class PreviewHoldValidator : AbstractValidator<PreviewHoldQuery>
{
    public PreviewHoldValidator() => RuleFor(x => x.Request).SetValidator(new HoldTargetsRequestValidator());
}

public sealed class PreviewHoldHandler(IAppDbContext db) : IRequestHandler<PreviewHoldQuery, HoldPreviewDto>
{
    public async Task<HoldPreviewDto> Handle(PreviewHoldQuery q, CancellationToken ct)
    {
        var sel = await db.SelectAsync(q.Request, ct);
        var ids = sel.SerialIds;
        var byStatus = await db.Serials.Where(s => ids.Contains(s.Id)).GroupBy(s => s.Status)
            .Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var shipped = await (from cs in db.CartonSerials
                             join c in db.Cartons on cs.CartonId equals c.Id
                             where cs.UnpackedAt == null && c.Status == CartonStatus.Shipped && ids.Contains(cs.SerialId)
                             select cs.SerialId).Distinct().CountAsync(ct);
        var lots = await db.MaterialLots.Where(l => sel.LotIds.Contains(l.Id)).OrderBy(l => l.LotNo).Select(l => l.LotNo).ToListAsync(ct);
        return new HoldPreviewDto(sel.Scope.ToString(), sel.ScopeRef, ids.Count,
            byStatus.OrderBy(b => b.Key).Select(b => new HoldStatusCountDto(b.Key.ToString(), b.Count)).ToList(),
            byStatus.Where(b => b.Key == SerialStatus.Hold).Sum(b => b.Count), shipped, sel.SkippedScrapped, lots, sel.SkippedLots);
    }
}

// ---------- Hold (QC, Supervisor, Admin) ----------

public sealed record HoldTargetsCommand(HoldTargetsRequest Request) : IRequest<HoldResultDto>;

public sealed class HoldTargetsValidator : AbstractValidator<HoldTargetsCommand>
{
    public HoldTargetsValidator()
    {
        RuleFor(x => x.Request).SetValidator(new HoldTargetsRequestValidator());
        RuleFor(x => x.Request.Reason).NotEmpty().MaximumLength(HoldRecord.ReasonMaxLength).WithName("Reason");   // Q9
        RuleFor(x => x.Request).Must(r => !(r.CreateNcr && r.NcrId is not null)).WithName("NcrId")
            .WithMessage("Either link an NCR or create one, not both.");
    }
}

// One transaction: (the new NCR), the HoldRecords, the serial and lot statuses, the audit row. The gates of M3
// (route check) and M6 (load verification, consumption) read Status: nothing else to tell them (spec 2.7).
public sealed class HoldTargetsHandler(IAppDbContext db, IHoldWriter writer, ISequenceNumbers sequences, ICurrentUser user, TimeProvider time)
    : IRequestHandler<HoldTargetsCommand, HoldResultDto>
{
    public async Task<HoldResultDto> Handle(HoldTargetsCommand cmd, CancellationToken ct)
    {
        var r = cmd.Request;
        var sel = await db.SelectAsync(r, ct);
        if (sel.IsEmpty) throw new DomainException($"Nothing to hold for {sel.Scope} {sel.ScopeRef}.");
        var by = user.EmployeeCode ?? throw new InvalidOperationException("A hold needs a logged-in user.");
        var now = time.GetUtcNow().UtcDateTime;
        var reason = r.Reason!.Trim();

        Ncr? ncr = null;
        if (r.NcrId is { } ncrId)
        {
            ncr = await db.Ncrs.AsNoTracking().SingleOrDefaultAsync(n => n.Id == ncrId, ct)
                  ?? throw new KeyNotFoundException($"NCR {ncrId} not found.");
            if (ncr.Status is not (NcrStatus.Open or NcrStatus.UnderReview))
                throw new DomainException($"NCR {ncr.NcrNo} is {ncr.Status}: hold for an NCR still under investigation.");
        }

        await using var tx = await db.BeginTransactionAsync(ct);
        if (r.CreateNcr)
        {
            ncr = NewNcr(sel, reason, await sequences.NextNcrNumberAsync(ct));
            db.Ncrs.Add(ncr);
            await db.SaveChangesAsync(ct);
        }
        var batch = new HoldBatch(Guid.NewGuid(), sel.Scope, sel.ScopeRef, reason, ncr?.Id, by, now);
        var (serials, lots) = await db.HoldAsync(writer, batch, sel.SerialIds, sel.LotIds, ct);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new HoldResultDto(batch.Id, serials, lots, ncr?.Id, ncr?.NcrNo);
    }

    // The NCR's targets are what its disposition will act on (user decision, step 9): the lots for a lot hold (its
    // boards stay held by this batch until someone releases them), the boards otherwise.
    private static Ncr NewNcr(HoldSelection sel, string reason, long number)
    {
        List<(HoldTargetType, long)> targets = sel.Scope == HoldScope.Lot
            ? sel.LotIds.Select(id => (HoldTargetType.Lot, (long)id)).ToList()
            : sel.SerialIds.Select(id => (HoldTargetType.Serial, id)).ToList();
        if (targets.Count == 0)
            throw new DomainException(sel.Scope == HoldScope.Lot
                ? "No lot of the family can be held (all consumed or waiting for IQC): hold without an NCR, or open one by hand."
                : "No board to hold.");
        if (targets.Count > HoldSelections.MaxSerialList)
            throw new DomainException($"{targets.Count} boards are too many for one NCR (at most {HoldSelections.MaxSerialList}): hold without an NCR, then open NCRs per lot or carton.");
        var source = sel.Scope is HoldScope.Lot or HoldScope.Carton or HoldScope.Pallet ? NcrSource.Traceability : NcrSource.Manual;
        var refText = sel.ScopeRef.Length > Ncr.RefMaxLength ? sel.ScopeRef[..Ncr.RefMaxLength] : sel.ScopeRef;
        return new Ncr(Ncr.FormatNo(number), source, refText, null, $"Hold {sel.Scope} {sel.ScopeRef}: {reason}",
            Math.Max(1, sel.SerialIds.Count), targets);
    }
}

// ---------- Release a batch (QC, Supervisor, Admin; Q9) ----------

public sealed record ReleaseHoldCommand(Guid BatchId, string Note) : IRequest<ReleaseResultDto>;

public sealed class ReleaseHoldValidator : AbstractValidator<ReleaseHoldCommand>
{
    public ReleaseHoldValidator() => RuleFor(x => x.Note).NotEmpty().MaximumLength(HoldRecord.ReasonMaxLength);
}

public sealed class ReleaseHoldHandler(IAppDbContext db, ICurrentUser user, TimeProvider time) : IRequestHandler<ReleaseHoldCommand, ReleaseResultDto>
{
    public async Task<ReleaseResultDto> Handle(ReleaseHoldCommand cmd, CancellationToken ct)
    {
        if (!await db.HoldRecords.AnyAsync(h => h.BatchId == cmd.BatchId, ct))
            throw new KeyNotFoundException($"Hold {cmd.BatchId} not found.");
        var by = user.EmployeeCode ?? throw new InvalidOperationException("A release needs a logged-in user.");
        await using var tx = await db.BeginTransactionAsync(ct);
        var (released, restored, still) = await db.ReleaseAsync(db.HoldRecords.Where(h => h.BatchId == cmd.BatchId), by, cmd.Note.Trim(),
            time.GetUtcNow().UtcDateTime, cmd.BatchId.ToString(), ct);
        // Two people releasing the same batch: the second finds nothing active (the first one's UPDATE committed).
        if (released == 0) throw new DomainException("This hold is already released.");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ReleaseResultDto(cmd.BatchId, released, restored, still);
    }
}

// ---------- Hold batches (QC, Supervisor, Admin) ----------

public sealed record GetHoldBatchesQuery(bool ActiveOnly = true) : IRequest<IReadOnlyList<HoldBatchDto>>;

public sealed class GetHoldBatchesHandler(IAppDbContext db) : IRequestHandler<GetHoldBatchesQuery, IReadOnlyList<HoldBatchDto>>
{
    public async Task<IReadOnlyList<HoldBatchDto>> Handle(GetHoldBatchesQuery q, CancellationToken ct)
    {
        var batches = from h in db.HoldRecords.AsNoTracking()
                      group h by new { h.BatchId, h.Scope, h.ScopeRef, h.Reason, h.NcrId, h.HeldBy, h.HeldAt } into g
                      select new
                      {
                          g.Key,
                          ActiveSerials = g.Count(h => h.ReleasedAt == null && h.TargetType == HoldTargetType.Serial),
                          ActiveLots = g.Count(h => h.ReleasedAt == null && h.TargetType == HoldTargetType.Lot),
                          Released = g.Count(h => h.ReleasedAt != null),
                      };
        if (q.ActiveOnly) batches = batches.Where(b => b.ActiveSerials + b.ActiveLots > 0);
        var rows = await batches.OrderByDescending(b => b.Key.HeldAt).Take(200).ToListAsync(ct);
        var ncrIds = rows.Where(r => r.Key.NcrId != null).Select(r => r.Key.NcrId!.Value).Distinct().ToList();
        var ncrNos = await db.Ncrs.Where(n => ncrIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.NcrNo, ct);
        return rows.Select(r => new HoldBatchDto(r.Key.BatchId, r.Key.Scope.ToString(), r.Key.ScopeRef, r.Key.Reason, r.Key.NcrId,
            r.Key.NcrId is { } id ? ncrNos.GetValueOrDefault(id) : null, r.ActiveSerials, r.ActiveLots, r.Released, r.Key.HeldBy, r.Key.HeldAt)).ToList();
    }
}
