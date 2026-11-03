using MediatR;
using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.Execution;
using MiniMes.Contracts.Packing;
using MiniMes.Domain.Packing;

namespace MiniMes.Application.Packing;

// GET /api/stations/{id}/packing-status?woId=: the open carton of (station, WO) and its serials, for the Packing
// screen (step 6). Without woId, the WO of the station's open session; no session = an empty status.
public sealed record GetPackingStatusQuery(int StationId, int? WorkOrderId) : IRequest<PackingStatusDto>;

public sealed class GetPackingStatusHandler(IAppDbContext db) : IRequestHandler<GetPackingStatusQuery, PackingStatusDto>
{
    public async Task<PackingStatusDto> Handle(GetPackingStatusQuery q, CancellationToken ct)
    {
        var station = await db.LoadStationAsync(q.StationId, ct);   // 404
        if ((q.WorkOrderId ?? await db.OpenSessionWoIdAsync(station.Id, ct)) is not { } woId)
            return new PackingStatusDto(station.Id, null, null, null, null, null);

        var wo = await (from w in db.WorkOrders
                        join i in db.Items on w.ItemId equals i.Id
                        where w.Id == woId
                        select new { w.WoNo, ItemId = i.Id, ItemCode = i.Code }).SingleOrDefaultAsync(ct)
                 ?? throw new KeyNotFoundException($"Work order {woId} not found.");
        var spec = await db.PackingSpecs.Where(p => p.ItemId == wo.ItemId).Select(p => new { p.QtyPerCarton, p.CartonsPerPallet }).SingleOrDefaultAsync(ct);

        var carton = await db.Cartons.AsNoTracking()
            .Where(c => c.StationId == station.Id && c.WorkOrderId == woId && c.Status == CartonStatus.Open)
            .Select(c => new { c.Id, c.CartonNo, c.Qty, c.OpenedAt }).SingleOrDefaultAsync(ct);
        OpenCartonDto? open = null;
        if (carton is not null)
        {
            var serials = await (from cs in db.CartonSerials
                                 join s in db.Serials on cs.SerialId equals s.Id
                                 where cs.CartonId == carton.Id && cs.UnpackedAt == null
                                 orderby cs.PackedAt descending, cs.Id descending
                                 select new PackedSerialDto(s.SerialNo, cs.PackedAt, cs.PackedBy)).ToListAsync(ct);
            open = new OpenCartonDto(carton.CartonNo, carton.Qty, carton.OpenedAt, serials);
        }
        return new PackingStatusDto(station.Id, woId, wo.WoNo, wo.ItemCode, spec?.QtyPerCarton, open, spec?.CartonsPerPallet,
            await OpenPalletAsync(station.Id, wo.ItemId, ct));
    }

    // Step 6: the pallet the Palletize mode fills (one Open pallet per station and item). Cartons newest first, with
    // their derived Hold (P10), though a held carton never gets on (P7): it shows if a board is held afterwards.
    private async Task<OpenPalletDto?> OpenPalletAsync(int stationId, int itemId, CancellationToken ct)
    {
        var pallet = await db.Pallets.AsNoTracking()
            .Where(p => p.StationId == stationId && p.ItemId == itemId && p.Status == PalletStatus.Open)
            .Select(p => new { p.Id, p.PalletNo, p.CartonCount }).SingleOrDefaultAsync(ct);
        if (pallet is null) return null;
        var cartons = await (from c in db.Cartons
                             join w in db.WorkOrders on c.WorkOrderId equals w.Id
                             where c.PalletId == pallet.Id
                             orderby c.Id descending
                             select new { c.Id, c.CartonNo, w.WoNo, c.Qty, c.IsPartial }).ToListAsync(ct);
        var held = await db.HeldCartonIdsAsync(cartons.Select(c => c.Id).ToList(), ct);
        return new OpenPalletDto(pallet.PalletNo, pallet.CartonCount,
            cartons.Select(c => new PalletCartonDto(c.CartonNo, c.WoNo, c.Qty, c.IsPartial, held.Contains(c.Id))).ToList());
    }
}
