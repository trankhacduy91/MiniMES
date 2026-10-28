using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MiniMes.Application.Oee;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Machines;
using MiniMes.Domain.Oee;
using MiniMes.Domain.Time;
using MiniMes.Infrastructure.Persistence;

namespace MiniMes.Infrastructure.Oee;

// Spec M5 2.5 / step 4. SQL does the heavy part (summing thousands of counter rows, counting scans); the few dozen
// state segments of a shift come back as rows and C# does the interval maths (OeeInputBuilder), where it is tested.
public sealed class OeeInputReader(AppDbContext db, TimeProvider time, IOptions<OeeOptions> options) : IOeeInputReader
{
    public async Task<OeeReading> ReadAsync(int machineId, ShiftWindow shift, Interval window, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var (start, end) = (window.StartUtc, window.EndUtc);
        var workCenterId = await db.Machines.Where(m => m.Id == machineId).Select(m => m.WorkCenterId).SingleAsync(ct);

        // (1) Total Count: the ingest already turned the cumulative counter into Delta with the reset rule
        // (CounterMath, M4), so a SUM is enough; no LAG over the raw counter at read time. MachineTs is the event
        // time (late gateway messages keep their own time), so a replayed count lands in the hour it happened.
        long total = await db.MachineEvents
            .Where(e => e.MachineId == machineId && e.Kind == MachineEventKind.Count && e.MachineTs >= start && e.MachineTs < end)
            .SumAsync(e => e.Delta ?? 0, ct);

        // (3) Good / NG from the serial scans at the station(s) doing this machine's work center.
        var stations = db.Stations.Where(s => s.WorkCenterId == workCenterId).Select(s => s.Id);
        var scans = await db.ScanLogs
            .Where(s => stations.Contains(s.StationId) && s.ScannedAt >= start && s.ScannedAt < end)
            .GroupBy(s => new { s.WorkOrderId, s.Result })
            .Select(g => new { g.Key.WorkOrderId, g.Key.Result, Count = g.LongCount() })
            .ToListAsync(ct);
        long good = scans.Where(s => s.Result == ScanResult.Ok).Sum(s => s.Count);
        // Idle counts as a loss only when the machine had work this shift (decision of step 4).
        bool hasPlan = scans.Count > 0 || await db.ScanLogs
            .AnyAsync(s => stations.Contains(s.StationId) && s.ScannedAt >= shift.StartUtc && s.ScannedAt < shift.EndUtc, ct);

        // (5) Ideal cycle time per WO from the WorkOrderOperation snapshot (M2): editing the routing later does
        // not change history. No scan in the window but the machine counted: the WO scanned last before it.
        var perWo = scans.GroupBy(s => s.WorkOrderId).ToDictionary(g => g.Key, g => g.Sum(x => x.Count));
        if (perWo.Count == 0 && total > 0)
        {
            var last = await db.ScanLogs.Where(s => stations.Contains(s.StationId) && s.ScannedAt < end)
                .OrderByDescending(s => s.ScannedAt).Select(s => (int?)s.WorkOrderId).FirstOrDefaultAsync(ct);
            if (last is { } woId) perWo[woId] = 0;
        }
        var woIds = perWo.Keys.ToList();
        var cycleTimes = (await db.WorkOrderOperations
                .Where(o => woIds.Contains(o.WorkOrderId) && o.WorkCenterId == workCenterId)
                .Select(o => new { o.WorkOrderId, o.Seq, o.IdealCycleTimeSec })
                .ToListAsync(ct))
            .GroupBy(o => o.WorkOrderId)
            .ToDictionary(g => g.Key, g => (double)g.OrderBy(o => o.Seq).First().IdealCycleTimeSec);
        var segments = WorkOrderShare.Split(total, perWo
            .Where(w => cycleTimes.ContainsKey(w.Key))
            .Select(w => new WorkOrderScans(w.Key, cycleTimes[w.Key], w.Value))
            .ToList());

        // (2) State segments overlapping the window, whole (the micro-stop threshold looks at the full stop).
        // An open segment (EndAt null) lasts until now.
        var history = await (
                from h in db.MachineStateHistory
                where h.MachineId == machineId && h.StartAt < end && (h.EndAt == null || h.EndAt > start)
                join r in db.DowntimeReasons on h.DowntimeReasonId equals r.Id into reasons
                from r in reasons.DefaultIfEmpty()
                select new { h.State, h.StartAt, h.EndAt, Counts = (bool?)r!.CountsAgainstAvailability })   // SQL LEFT JOIN: null when no reason
            .ToListAsync(ct);
        var plannedStops = new List<Interval>();
        var downtime = new List<Interval>();
        foreach (var h in history)
        {
            var span = new Interval(h.StartAt, h.EndAt ?? now);
            switch (StopRules.Treat(h.State, h.Counts, hasPlan))
            {
                case StopTreatment.NotPlanned: plannedStops.Add(span); break;
                case StopTreatment.Downtime: downtime.Add(span); break;
            }
        }

        // (4) Breaks come from the shift definition (M1), already in UTC on the window.
        var input = OeeInputBuilder.Build(window, shift.Breaks, plannedStops, downtime,
            TimeSpan.FromSeconds(options.Value.MicroStopThresholdSec), segments, good);
        return new OeeReading(input, scans.Sum(s => s.Count));
    }
}
