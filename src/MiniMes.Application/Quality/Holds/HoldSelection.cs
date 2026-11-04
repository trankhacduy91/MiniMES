using FluentValidation;
using FluentValidation.Results;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Quality;
using MiniMes.Contracts.Traceability;
using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Material;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;

namespace MiniMes.Application.Quality.Holds;

// What one Hold request points at, resolved to Ids (spec 2.6: only the SELECT differs between the scopes, the writes
// are the same). Serials: not Scrapped (Q10). Lots: Available or already Hold; the rest of a lot family is reported.
internal sealed record HoldSelection(
    HoldScope Scope,
    string ScopeRef,
    List<long> SerialIds,
    List<int> LotIds,
    List<SkippedLotDto> SkippedLots,
    int SkippedScrapped)
{
    public bool IsEmpty => SerialIds.Count == 0 && LotIds.Count == 0;
}

// The fields each scope needs (shared by preview and hold).
public sealed class HoldTargetsRequestValidator : AbstractValidator<HoldTargetsRequest>
{
    public HoldTargetsRequestValidator()
    {
        RuleFor(x => x.Scope).Must(s => Enum.TryParse<HoldScope>(s, ignoreCase: true, out _))
            .WithMessage($"Scope must be one of: {string.Join(", ", Enum.GetNames<HoldScope>())}.");
        When(x => Is(x, HoldScope.Lot), () => RuleFor(x => x.LotNo).NotEmpty().MaximumLength(MaterialLot.LotNoMaxLength));
        When(x => Is(x, HoldScope.Serials), () => RuleFor(x => x.SerialNos)
            .Must(s => s is { Count: > 0 and <= HoldSelections.MaxSerialList })
            .WithMessage($"Give 1 to {HoldSelections.MaxSerialList} serial numbers (more: hold by lot, carton or pallet)."));
        When(x => Is(x, HoldScope.Carton), () => RuleFor(x => x.CartonNo).NotEmpty().MaximumLength(30));
        When(x => Is(x, HoldScope.Pallet), () => RuleFor(x => x.PalletNo).NotEmpty().MaximumLength(30));
        When(x => Is(x, HoldScope.MachineWindow), () =>
        {
            RuleFor(x => x.MachineCode).NotEmpty().MaximumLength(50);
            RuleFor(x => x.From).NotNull();
            RuleFor(x => x.To).NotNull().Must((x, to) => x.From < to && to - x.From <= HoldSelections.MaxWindow)
                .WithMessage($"To must be after From, at most {HoldSelections.MaxWindow.TotalDays:0} days later.");
        });
    }

    private static bool Is(HoldTargetsRequest r, HoldScope scope)
        => Enum.TryParse<HoldScope>(r.Scope, ignoreCase: true, out var s) && s == scope;
}

internal static class HoldSelections
{
    public const int MaxSerialList = 500;
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(7);
    private const int MaxGenealogyDepth = 10;   // same limit as the trace queries (M6)

    public static async Task<HoldSelection> SelectAsync(this IAppDbContext db, HoldTargetsRequest r, CancellationToken ct)
    {
        var scope = Enum.Parse<HoldScope>(r.Scope, ignoreCase: true);
        return scope switch
        {
            HoldScope.Lot => await LotAsync(db, Code(r.LotNo!), ct),
            HoldScope.Serials => await SerialListAsync(db, r.SerialNos!, ct),
            HoldScope.Carton => await PackedAsync(db, scope, Code(r.CartonNo!), ct),
            HoldScope.Pallet => await PackedAsync(db, scope, Code(r.PalletNo!), ct),
            _ => await MachineWindowAsync(db, Code(r.MachineCode!), AsUtc(r.From!.Value), AsUtc(r.To!.Value), ct),
        };
    }

    // The lot, every lot of the same delivery (supplier lot + item) and the lots split from them (M6 spec 1.5 "hold too
    // narrow"), and every serial any of them went into (SerialComponent), whatever the lot's own status: a Consumed
    // reel is not held itself, but its boards are.
    private static async Task<HoldSelection> LotAsync(IAppDbContext db, string lotNo, CancellationToken ct)
    {
        var start = await db.MaterialLots.AsNoTracking().SingleOrDefaultAsync(l => l.LotNo == lotNo, ct)
                    ?? throw new KeyNotFoundException($"Lot {lotNo} not found.");
        var lots = new Dictionary<int, MaterialLot> { [start.Id] = start };
        if (start.SupplierLot is { } supplierLot)
            foreach (var l in await db.MaterialLots.AsNoTracking().Where(l => l.SupplierLot == supplierLot && l.ItemId == start.ItemId).ToListAsync(ct))
                lots.TryAdd(l.Id, l);
        var frontier = lots.Keys.ToList();
        for (var depth = 0; depth < MaxGenealogyDepth && frontier.Count > 0; depth++)
        {
            var childIds = await db.LotGenealogy.Where(g => frontier.Contains(g.ParentLotId)).Select(g => g.ChildLotId).ToListAsync(ct);
            frontier = childIds.Where(id => !lots.ContainsKey(id)).Distinct().ToList();
            foreach (var l in await db.MaterialLots.AsNoTracking().Where(l => frontier.Contains(l.Id)).ToListAsync(ct)) lots.TryAdd(l.Id, l);
        }

        var familyIds = lots.Keys.ToList();
        var serials = db.SerialComponents.Where(c => familyIds.Contains(c.LotId)).Select(c => c.SerialId).Distinct();
        var (held, scrapped) = await SplitScrappedAsync(db, serials, ct);
        var holdable = lots.Values.Where(l => l.Status is MaterialLotStatus.Available or MaterialLotStatus.Hold).OrderBy(l => l.LotNo).ToList();
        var skipped = lots.Values.Except(holdable).OrderBy(l => l.LotNo).Select(l => new SkippedLotDto(l.LotNo, l.Status.ToString())).ToList();
        return new HoldSelection(HoldScope.Lot, lotNo, held, holdable.Select(l => l.Id).ToList(), skipped, scrapped);
    }

    private static async Task<HoldSelection> SerialListAsync(IAppDbContext db, IReadOnlyList<string> serialNos, CancellationToken ct)
    {
        var codes = serialNos.Select(Code).Where(c => c.Length > 0).Distinct().ToList();
        var found = await db.Serials.AsNoTracking().Where(s => codes.Contains(s.SerialNo))
            .Select(s => new { s.Id, s.SerialNo, s.Status }).ToListAsync(ct);
        var unknown = codes.Except(found.Select(f => f.SerialNo)).ToList();
        if (unknown.Count > 0)
            throw new ValidationException([new ValidationFailure("SerialNos", "Not found: " + string.Join(", ", unknown))]);
        var scopeRef = codes.Count == 1 ? codes[0] : $"{codes[0]} +{codes.Count - 1}";
        return new HoldSelection(HoldScope.Serials, scopeRef, found.Where(f => f.Status != SerialStatus.Scrapped).Select(f => f.Id).ToList(),
            [], [], found.Count(f => f.Status == SerialStatus.Scrapped));
    }

    // [v2] (a) the boards packed in the carton, or in every carton on the pallet (unpacked ones are out). A shipped
    // pallet is held too: the data must say so, and the preview warns "contains shipped units".
    private static async Task<HoldSelection> PackedAsync(IAppDbContext db, HoldScope scope, string code, CancellationToken ct)
    {
        IQueryable<Carton> cartons;
        if (scope == HoldScope.Carton)
        {
            if (!await db.Cartons.AnyAsync(c => c.CartonNo == code, ct)) throw new KeyNotFoundException($"Carton {code} not found.");
            cartons = db.Cartons.Where(c => c.CartonNo == code);
        }
        else
        {
            var palletId = await db.Pallets.Where(p => p.PalletNo == code).Select(p => (int?)p.Id).SingleOrDefaultAsync(ct)
                           ?? throw new KeyNotFoundException($"Pallet {code} not found.");
            cartons = db.Cartons.Where(c => c.PalletId == palletId);
        }
        var serials = from cs in db.CartonSerials
                      join c in cartons on cs.CartonId equals c.Id
                      where cs.UnpackedAt == null
                      select cs.SerialId;
        var (held, scrapped) = await SplitScrappedAsync(db, serials.Distinct(), ct);
        return new HoldSelection(scope, code, held, [], [], scrapped);
    }

    // [v2] (b) SPC on a machine (2.3b) has no serial: the boards scanned OK at a station of the machine's work center
    // in [from, to). A bit wider than the boards really inside the machine; the user adds a margin (containment:
    // too many rather than one missing).
    private static async Task<HoldSelection> MachineWindowAsync(IAppDbContext db, string machineCode, DateTime from, DateTime to, CancellationToken ct)
    {
        var workCenterId = await db.Machines.Where(m => m.Code == machineCode).Select(m => (int?)m.WorkCenterId).SingleOrDefaultAsync(ct)
                           ?? throw new KeyNotFoundException($"Machine {machineCode} not found.");
        var stationIds = db.Stations.Where(s => s.WorkCenterId == workCenterId).Select(s => s.Id);
        var serials = db.ScanLogs.Where(l => stationIds.Contains(l.StationId) && l.Result == ScanResult.Ok
                                             && l.ScannedAt >= from && l.ScannedAt < to)
            .Select(l => l.SerialId).Distinct();
        var (held, scrapped) = await SplitScrappedAsync(db, serials, ct);
        return new HoldSelection(HoldScope.MachineWindow, $"{machineCode} {from:yyyy-MM-dd HH:mm}Z-{to:yyyy-MM-dd HH:mm}Z", held, [], [], scrapped);
    }

    private static async Task<(List<long> Holdable, int Scrapped)> SplitScrappedAsync(IAppDbContext db, IQueryable<long> serialIds, CancellationToken ct)
    {
        var rows = await db.Serials.Where(s => serialIds.Contains(s.Id)).Select(s => new { s.Id, s.Status }).ToListAsync(ct);
        return (rows.Where(r => r.Status != SerialStatus.Scrapped).Select(r => r.Id).ToList(), rows.Count(r => r.Status == SerialStatus.Scrapped));
    }

    private static string Code(string value) => value.Trim().ToUpperInvariant();

    private static DateTime AsUtc(DateTime value)
        => value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
