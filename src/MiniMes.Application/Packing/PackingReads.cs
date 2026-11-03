using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Packing;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Packing;

// Spec M3b 2.4: Hold of a carton/pallet is derived, never stored (P10). One source of truth: Serial.Status (M7 Hold
// writes there). Only live rows count ("UnpackedAt IS NULL": a held board taken out no longer holds the carton).
internal static class PackingHolds
{
    // Cartons, among the given ones, holding at least one serial on Hold: one EXISTS-style query for a whole pallet.
    public static async Task<HashSet<int>> HeldCartonIdsAsync(this IAppDbContext db, IReadOnlyCollection<int> cartonIds, CancellationToken ct)
        => (await (from cs in db.CartonSerials
                   join s in db.Serials on cs.SerialId equals s.Id
                   where cartonIds.Contains(cs.CartonId) && cs.UnpackedAt == null && s.Status == SerialStatus.Hold
                   select cs.CartonId).Distinct().ToListAsync(ct)).ToHashSet();

    public static Task<bool> IsCartonOnHoldAsync(this IAppDbContext db, int cartonId, CancellationToken ct)
        => db.CartonSerials.AnyAsync(cs => cs.CartonId == cartonId && cs.UnpackedAt == null
                                           && db.Serials.Any(s => s.Id == cs.SerialId && s.Status == SerialStatus.Hold), ct);
}

// The carton/pallet as the screens and the action endpoints show it.
internal static class PackingReads
{
    public static string Key(string code) => code.Trim().ToUpperInvariant();

    public static async Task<CartonDto> CartonDtoAsync(this IAppDbContext db, string cartonNo, CancellationToken ct, string? labelZpl = null)
    {
        var key = Key(cartonNo);
        var c = await (from carton in db.Cartons.AsNoTracking()
                       join w in db.WorkOrders on carton.WorkOrderId equals w.Id
                       join i in db.Items on carton.ItemId equals i.Id
                       where carton.CartonNo == key
                       select new
                       {
                           carton.Id, carton.CartonNo, w.WoNo, ItemCode = i.Code, carton.Qty, carton.Status, carton.IsPartial,
                           PalletNo = db.Pallets.Where(p => p.Id == carton.PalletId).Select(p => p.PalletNo).FirstOrDefault(),
                           carton.OpenedAt, carton.ClosedAt, carton.ClosedBy, carton.CloseReason,
                       }).SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException($"Carton {key} not found.");
        var serials = await (from cs in db.CartonSerials
                             join s in db.Serials on cs.SerialId equals s.Id
                             where cs.CartonId == c.Id && cs.UnpackedAt == null
                             orderby cs.PackedAt descending, cs.Id descending
                             select new PackedSerialDto(s.SerialNo, cs.PackedAt, cs.PackedBy)).ToListAsync(ct);
        return new CartonDto(c.CartonNo, c.WoNo, c.ItemCode, c.Qty, c.Status.ToString(), c.IsPartial, c.PalletNo,
            await db.IsCartonOnHoldAsync(c.Id, ct), c.OpenedAt, c.ClosedAt, c.ClosedBy, c.CloseReason, serials, labelZpl);
    }

    public static async Task<PalletDto> PalletDtoAsync(this IAppDbContext db, string palletNo, CancellationToken ct, string? labelZpl = null)
    {
        var key = Key(palletNo);
        var p = await (from pallet in db.Pallets.AsNoTracking()
                       join i in db.Items on pallet.ItemId equals i.Id
                       where pallet.PalletNo == key
                       select new
                       {
                           pallet.Id, pallet.PalletNo, ItemCode = i.Code, pallet.CartonCount, pallet.Status, pallet.IsPartial,
                           pallet.ClosedAt, pallet.ShipmentRef, pallet.ShippedAt, pallet.ShippedBy,
                       }).SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException($"Pallet {key} not found.");
        var cartons = await (from c in db.Cartons.AsNoTracking()
                             join w in db.WorkOrders on c.WorkOrderId equals w.Id
                             where c.PalletId == p.Id
                             orderby c.CartonNo
                             select new { c.Id, c.CartonNo, w.WoNo, c.Qty, c.IsPartial }).ToListAsync(ct);
        var held = await db.HeldCartonIdsAsync(cartons.Select(c => c.Id).ToList(), ct);
        return new PalletDto(p.PalletNo, p.ItemCode, p.CartonCount, cartons.Sum(c => c.Qty), p.Status.ToString(), p.IsPartial,
            held.Count > 0, p.ClosedAt, p.ShipmentRef, p.ShippedAt, p.ShippedBy,
            cartons.Select(c => new PalletCartonDto(c.CartonNo, c.WoNo, c.Qty, c.IsPartial, held.Contains(c.Id))).ToList(), labelZpl);
    }
}

public sealed record GetCartonQuery(string CartonNo) : IRequest<CartonDto>;

public sealed class GetCartonHandler(IAppDbContext db) : IRequestHandler<GetCartonQuery, CartonDto>
{
    public Task<CartonDto> Handle(GetCartonQuery q, CancellationToken ct) => db.CartonDtoAsync(q.CartonNo, ct);
}

public sealed record GetPalletQuery(string PalletNo) : IRequest<PalletDto>;

public sealed class GetPalletHandler(IAppDbContext db) : IRequestHandler<GetPalletQuery, PalletDto>
{
    public Task<PalletDto> Handle(GetPalletQuery q, CancellationToken ct) => db.PalletDtoAsync(q.PalletNo, ct);
}
