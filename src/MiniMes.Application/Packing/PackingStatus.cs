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
        var qtyPerCarton = await db.PackingSpecs.Where(p => p.ItemId == wo.ItemId).Select(p => (int?)p.QtyPerCarton).SingleOrDefaultAsync(ct);

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
        return new PackingStatusDto(station.Id, woId, wo.WoNo, wo.ItemCode, qtyPerCarton, open);
    }
}
