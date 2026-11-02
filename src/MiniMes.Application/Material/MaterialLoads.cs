using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.Execution;
using MiniMes.Contracts.Material;
using MiniMes.Domain.Common;
using MiniMes.Domain.Material;
using MiniMes.Domain.Users;

namespace MiniMes.Application.Material;

// What a station needs and holds, read by the load handler and the checklist query.
internal static class StationMaterials
{
    // The open session's WO, or 422: a load is verified against a WO's BOM snapshot, so there must be one.
    public static async Task<int> SessionWoIdAsync(this IAppDbContext db, ScanContextReader.StationRow station, CancellationToken ct)
        => await db.OpenSessionWoIdAsync(station.Id, ct)
           ?? throw new DomainException($"No work order is open at station {station.Code}: choose one first.");

    // Routing steps of the WO done at this station (its work center).
    public static Task<List<int>> StationSeqsAsync(this IAppDbContext db, int woId, int? workCenterId, CancellationToken ct)
        => db.WorkOrderOperations.AsNoTracking().Where(o => o.WorkOrderId == woId && o.WorkCenterId == workCenterId)
            .Select(o => o.Seq).ToListAsync(ct);

    // The WO's lot-controlled materials (snapshot, M2). Screws and tape are not traced (IsLotControlled = false).
    public static Task<List<WoMaterialRequirement>> RequirementsAsync(this IAppDbContext db, int woId, CancellationToken ct)
        => (from m in db.WorkOrderMaterials.AsNoTracking()
            join i in db.Items on m.ComponentItemId equals i.Id
            where m.WorkOrderId == woId && i.IsLotControlled
            select new WoMaterialRequirement(m.ComponentItemId, m.OperationSeq, m.QtyPerUnit)).ToListAsync(ct);

    public static async Task<List<StationMaterialDto>> ChecklistAsync(this IAppDbContext db, ScanContextReader.StationRow station,
        CancellationToken ct)
    {
        var required = new List<WoMaterialRequirement>();
        if (await db.OpenSessionWoIdAsync(station.Id, ct) is { } woId)
        {
            var seqs = await db.StationSeqsAsync(woId, station.WorkCenterId, ct);
            required = (await db.RequirementsAsync(woId, ct)).Where(r => seqs.Contains(r.OperationSeq)).ToList();
        }
        var loads = await (from l in db.MaterialLoads.AsNoTracking()
                           join lot in db.MaterialLots on l.LotId equals lot.Id
                           where l.StationId == station.Id && l.UnloadedAt == null
                           select new { l.Id, l.ItemId, lot.LotNo, lot.RemainingQty, l.SlotNo, l.LoadedBy, l.LoadedAt, l.FifoOverrideBy })
                          .ToListAsync(ct);
        var itemIds = required.Select(r => r.ItemId).Concat(loads.Select(l => l.ItemId)).Distinct().ToList();
        var items = await db.Items.AsNoTracking().Where(i => itemIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);

        return itemIds.Select(id =>
            {
                var load = loads.SingleOrDefault(l => l.ItemId == id);   // R6: at most one open load per item
                var req = required.FirstOrDefault(r => r.ItemId == id);
                return new StationMaterialDto(id, items[id].Code, items[id].Name, items[id].Uom, req is not null, req?.QtyPerUnit,
                    load?.Id, load?.LotNo, load?.RemainingQty, load?.SlotNo, load?.LoadedBy, load?.LoadedAt, load?.FifoOverrideBy);
            })
            .OrderByDescending(r => r.Required).ThenBy(r => r.ItemCode)
            .ToList();
    }
}

// ---------- POST /api/stations/{id}/material-loads: put a reel on the station ----------

public sealed record LoadMaterialCommand(int StationId, string LotNo, string? SlotNo, string? SupervisorBadge, string? OverrideReason)
    : IRequest<LoadMaterialResponse>;

public sealed class LoadMaterialValidator : AbstractValidator<LoadMaterialCommand>
{
    public LoadMaterialValidator()
    {
        RuleFor(x => x.LotNo).NotEmpty().MaximumLength(100);   // a wrong label is logged (cut), not refused as 400
        RuleFor(x => x.SlotNo).MaximumLength(MaterialLoad.SlotNoMaxLength);
        RuleFor(x => x.OverrideReason).NotEmpty().MaximumLength(MaterialLoad.ReasonMaxLength).When(x => x.SupervisorBadge is not null)
            .WithMessage("A FIFO exception needs a reason.");
        RuleFor(x => x.SupervisorBadge).NotEmpty().When(x => x.OverrideReason is not null)
            .WithMessage("A FIFO exception needs a supervisor's badge.");
    }
}

// Spec M6 step 4 / 2.4: load -> verify (pure MaterialVerifier) -> Accepted: close the reel of the same item on this
// station, open the new load; Warning: wait for a supervisor (second request); Rejected: nothing loaded. Every attempt
// is logged (R13). Two kiosks loading the same item at once: the filtered unique index makes one of them a 409.
public sealed class LoadMaterialHandler(IAppDbContext db, ICurrentUser user, TimeProvider time)
    : IRequestHandler<LoadMaterialCommand, LoadMaterialResponse>
{
    public async Task<LoadMaterialResponse> Handle(LoadMaterialCommand cmd, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var by = user.EmployeeCode ?? throw new InvalidOperationException("Loading material needs a logged-in user.");
        var station = await db.LoadStationAsync(cmd.StationId, ct);   // 404
        var woId = await db.SessionWoIdAsync(station, ct);             // 422
        var lotNo = cmd.LotNo.Trim().ToUpperInvariant();

        var entity = await db.MaterialLots.SingleOrDefaultAsync(l => l.LotNo == lotNo, ct);
        var lot = entity is null ? null : LotFacts.Of(entity);
        var seqs = await db.StationSeqsAsync(woId, station.WorkCenterId, ct);
        var requirements = await db.RequirementsAsync(woId, ct);
        // FIFO candidates: same item, could be loaded instead. A reel on ANOTHER station is not on the shelf; the reel
        // on THIS station stays a candidate: replacing a reel that still has parts is exactly what FIFO should question.
        var candidates = lot is null ? [] : await db.MaterialLots.AsNoTracking()
            .Where(l => l.ItemId == lot.ItemId && l.Id != lot.Id && l.Status == MaterialLotStatus.Available && l.RemainingQty > 0
                        && !db.MaterialLoads.Any(m => m.LotId == l.Id && m.UnloadedAt == null && m.StationId != station.Id))
            .Select(l => new LotFacts(l.Id, l.LotNo, l.ItemId, l.RemainingQty, l.ReceivedAt, l.ExpiryAt, l.Status))
            .ToListAsync(ct);

        var verdict = MaterialVerifier.Verify(lot, requirements, seqs, candidates, now);
        var supervisor = verdict.Outcome == VerifyOutcome.Warning && cmd.SupervisorBadge is { } badge
            ? await SupervisorAsync(badge, ct) : null;

        db.MaterialVerificationLogs.Add(new MaterialVerificationLog(station.Id, lotNo, lot?.Id, lot?.ItemId, woId, verdict.Outcome,
            verdict.Code, verdict.SuggestedLotNo, by, now, supervisor, supervisor is null ? null : cmd.OverrideReason!.Trim()));

        if (verdict.Outcome == VerifyOutcome.Rejected || (verdict.Outcome == VerifyOutcome.Warning && supervisor is null))
        {
            await db.SaveChangesAsync(ct);   // the attempt is evidence even when nothing is loaded (R13)
            return new LoadMaterialResponse(verdict.Outcome.ToString(), verdict.Code.ToString(), Message(verdict, lotNo), verdict.SuggestedLotNo, null);
        }

        // R6: one reel per (station, item). The same reel scanned again stays as it is.
        var open = await db.MaterialLoads.Where(m => m.ItemId == lot!.ItemId && m.UnloadedAt == null
                                                     && (m.StationId == station.Id || m.LotId == lot.Id)).ToListAsync(ct);
        if (open.SingleOrDefault(m => m.StationId == station.Id && m.LotId == lot!.Id) is { } same)
        {
            await db.SaveChangesAsync(ct);
            return new LoadMaterialResponse(nameof(VerifyOutcome.Accepted), verdict.Code.ToString(),
                $"Lot {lotNo} is already on station {station.Code}.", null, await LoadedAsync(station, same.ItemId, ct));
        }
        // The reel this station had for the item comes off; a reel moved here from another station leaves that one.
        foreach (var m in open) m.Unload(by, now);
        db.MaterialLoads.Add(new MaterialLoad(station.Id, lot!.ItemId, lot.Id, woId, cmd.SlotNo, by, now, supervisor,
            supervisor is null ? null : cmd.OverrideReason));
        await db.SaveChangesAsync(ct);   // unload + load + log: one transaction; a racing kiosk gets 409

        var message = supervisor is null ? $"Lot {lotNo} loaded on {station.Code}."
            : $"Lot {lotNo} loaded on {station.Code}; FIFO exception approved by {supervisor}.";
        return new LoadMaterialResponse(nameof(VerifyOutcome.Accepted), verdict.Code.ToString(), message, verdict.SuggestedLotNo,
            await LoadedAsync(station, lot.ItemId, ct));
    }

    // R5: the badge must be an active Supervisor. Wrong badge = 400 on the field, the warning stands.
    private async Task<string> SupervisorAsync(string badge, CancellationToken ct)
    {
        var code = badge.Trim();
        return await db.Users.Where(u => u.EmployeeCode == code && u.IsActive && u.Roles.Any(r => r.Role == Role.Supervisor))
                   .Select(u => u.EmployeeCode).SingleOrDefaultAsync(ct)   // as stored (the column is case-insensitive)
               ?? throw new ValidationException([new ValidationFailure(nameof(LoadMaterialCommand.SupervisorBadge),
                   $"'{code}' is not an active supervisor's badge.")]);
    }

    private async Task<StationMaterialDto> LoadedAsync(ScanContextReader.StationRow station, int itemId, CancellationToken ct)
        => (await db.ChecklistAsync(station, ct)).Single(r => r.ItemId == itemId);

    private static string Message(VerifyResult v, string lotNo) => v.Code switch
    {
        VerifyCode.LotNotFound => $"No lot '{lotNo}'.",
        VerifyCode.ItemNotInBom => $"The item of lot {lotNo} is not in the work order's BOM.",
        VerifyCode.ItemNotForStation => $"The item of lot {lotNo} is used at another step, not at this station.",
        VerifyCode.LotOnHold => $"Lot {lotNo} is on hold.",
        VerifyCode.LotNotIqcPassed => $"Lot {lotNo} has not passed incoming inspection (IQC).",
        VerifyCode.LotEmpty => $"Lot {lotNo} is empty.",
        VerifyCode.LotExpired => $"Lot {lotNo} is expired.",
        VerifyCode.FifoOlderLotAvailable => $"Older lot {v.SuggestedLotNo} of the same item is available: load it first.",
        _ => $"Lot {lotNo} can be loaded.",
    };
}

// ---------- GET /api/stations/{id}/material-loads: the station's checklist ----------

public sealed record GetStationMaterialsQuery(int StationId) : IRequest<IReadOnlyList<StationMaterialDto>>;

public sealed class GetStationMaterialsHandler(IAppDbContext db) : IRequestHandler<GetStationMaterialsQuery, IReadOnlyList<StationMaterialDto>>
{
    public async Task<IReadOnlyList<StationMaterialDto>> Handle(GetStationMaterialsQuery q, CancellationToken ct)
        => await db.ChecklistAsync(await db.LoadStationAsync(q.StationId, ct), ct);
}

// ---------- POST /api/stations/{id}/material-loads/{loadId}/unload ----------

public sealed record UnloadMaterialCommand(int StationId, int LoadId, decimal? RemainingQty) : IRequest<IReadOnlyList<StationMaterialDto>>;

public sealed class UnloadMaterialValidator : AbstractValidator<UnloadMaterialCommand>
{
    public UnloadMaterialValidator() => RuleFor(x => x.RemainingQty).GreaterThanOrEqualTo(0).When(x => x.RemainingQty is not null);
}

// R10: taking a reel off. The operator may enter what is really left (the theoretical number drifts with pick losses);
// 0 = the lot is Consumed. The lot change is audited (MaterialLot is IAudited).
public sealed class UnloadMaterialHandler(IAppDbContext db, ICurrentUser user, TimeProvider time)
    : IRequestHandler<UnloadMaterialCommand, IReadOnlyList<StationMaterialDto>>
{
    public async Task<IReadOnlyList<StationMaterialDto>> Handle(UnloadMaterialCommand cmd, CancellationToken ct)
    {
        var station = await db.LoadStationAsync(cmd.StationId, ct);
        var load = await db.MaterialLoads.SingleOrDefaultAsync(l => l.Id == cmd.LoadId && l.StationId == station.Id, ct)
                   ?? throw new KeyNotFoundException($"Load {cmd.LoadId} not found at station {station.Code}.");
        load.Unload(user.EmployeeCode ?? throw new InvalidOperationException("Unloading needs a logged-in user."),
            time.GetUtcNow().UtcDateTime);   // 422 when already unloaded
        if (cmd.RemainingQty is { } actual)
            (await db.MaterialLots.SingleAsync(l => l.Id == load.LotId, ct)).CorrectRemaining(actual);   // 422 above Qty
        await db.SaveChangesAsync(ct);
        return await db.ChecklistAsync(station, ct);
    }
}
