using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Execution;

// The reads of the hot path, all AsNoTracking and by exact key (spec 2.2): the station, its open session, the
// serial with its WO's status and FAI columns, the WO's frozen routing, the serial's last scan at this station.
// Station and routing barely change and could be cached (IMemoryCache); measured first in step 10.
internal static class ScanContextReader
{
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(5);

    public sealed record StationRow(int Id, string Code, int? WorkCenterId, StationType Type);

    public static async Task<StationRow> LoadStationAsync(this IAppDbContext db, int stationId, CancellationToken ct)
        => await db.Stations.AsNoTracking().Where(s => s.Id == stationId && s.IsActive)
               .Select(s => new StationRow(s.Id, s.Code, s.WorkCenterId, s.Type)).SingleOrDefaultAsync(ct)
           ?? throw new KeyNotFoundException($"Station {stationId} not found or inactive.");

    public static Task<int?> OpenSessionWoIdAsync(this IAppDbContext db, int stationId, CancellationToken ct)
        => db.StationSessions.AsNoTracking().Where(s => s.StationId == stationId && s.ClosedAt == null)
            .Select(s => (int?)s.WorkOrderId).SingleOrDefaultAsync(ct);

    public static async Task<ScanContext> LoadContextAsync(this IAppDbContext db, StationRow station, int sessionWoId,
        string serialNo, ScanResult requested, DateTime nowUtc, CancellationToken ct)
    {
        var row = await (from s in db.Serials.AsNoTracking()
                         join w in db.WorkOrders on s.WorkOrderId equals w.Id
                         where s.SerialNo == serialNo
                         select new { s.SerialNo, s.WorkOrderId, s.Status, s.CurrentSeq, WoStatus = w.Status, w.FaiStatus, w.FaiSerialNo })
                        .SingleOrDefaultAsync(ct);
        var stationInfo = new StationInfo(station.Id, station.WorkCenterId, station.Type == StationType.Repair);
        if (row is null)
            return new ScanContext(null, null, sessionWoId, stationInfo, [], null, null, null, requested, nowUtc, DuplicateWindow);

        var steps = await db.WorkOrderOperations.AsNoTracking().Where(o => o.WorkOrderId == row.WorkOrderId)
            .Select(o => new RouteStep(o.Seq, o.OperationCode, o.WorkCenterId)).ToListAsync(ct);
        // Index (SerialNo, ScannedAt): a seek, then the newest row of this station.
        var last = await db.ScanLogs.AsNoTracking().Where(l => l.SerialNo == serialNo && l.StationId == station.Id)
            .OrderByDescending(l => l.ScannedAt).Select(l => new LastScan(l.ScannedAt, l.Result)).FirstOrDefaultAsync(ct);

        return new ScanContext(new SerialState(row.SerialNo, row.WorkOrderId, row.Status, row.CurrentSeq), row.WoStatus,
            sessionWoId, stationInfo, steps, row.FaiStatus, row.FaiSerialNo, last, requested, nowUtc, DuplicateWindow);
    }
}
