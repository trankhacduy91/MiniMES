using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Common;
using MiniMes.Application.Oee;
using MiniMes.Application.Realtime;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Factory;
using MiniMes.Domain.Machines;
using MiniMes.Domain.Planning;
using MiniMes.Infrastructure.Oee;

namespace MiniMes.Infrastructure.Persistence.Seed;

// Development only (spec M5 step 8 check: "choose the sample shift, see 79.8 %"). The sample shift of spec 1.2.2 on
// L1-SMT-01, shift S1 of Thursday 01/10/2026, written as raw rows (counter messages, state segments, scans), then
// closed by the real snapshot service, so the dashboard shows it like any other shift. The seeded S1 breaks are
// 10:00-10:15 and 11:30-12:15 (60 minutes, like the spec's), so the stops are placed around them.
// Step 9 adds two shifts with wrong data, to see the warning badges on the dashboard:
//   Fri 02/10: the machine really makes a board every 15 s of run time, the WO says 20 s -> 1,440 boards in 360
//              run minutes, P = 133 % (rule 9);
//   Mon 05/10: the station scanner stops at about 13:38, 940 scans for 1,000 counts -> CounterMismatch, Q 91 % (1.2.10).
// Rows written once per shift (skipped when its work order exists); missing snapshots closed on every start.
public class OeeDemoSeeder(AppDbContext db, PlantTimeZone plant, IRealtimeNotifier notifier, ILoggerFactory logs)
{
    public const string WorkOrderNo = "WO-OEE-DEMO";
    public static readonly DateOnly ShiftDate = new(2026, 10, 1);

    // Boards are made only while Running, evenly over the Running time, and counted 5 per counter message; the first
    // Scans boards are scanned (when the message that counted them is sent), one in every Scans / Ng of them NG.
    public sealed record DemoShift(string WoNo, DateOnly Date, string SerialPrefix, int Boards, int Scans, int Ng);

    public static readonly DemoShift Sample = new(WorkOrderNo, ShiftDate, "OEEDEMO", 1000, 1000, 30);
    public static readonly DemoShift FastMachine = new("WO-OEE-FAST", new(2026, 10, 2), "OEEFAST", 1440, 1440, 43);
    public static readonly DemoShift MissingScans = new("WO-OEE-MISS", new(2026, 10, 5), "OEEMISS", 1000, 940, 30);

    private sealed class FixedTime(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }

    public async Task SeedAsync(CancellationToken ct = default)
    {
        foreach (var shift in new[] { Sample, FastMachine, MissingScans })
            await SeedShiftAsync(shift, ct);
    }

    private async Task SeedShiftAsync(DemoShift demo, CancellationToken ct)
    {
        var machine = await db.Machines.SingleOrDefaultAsync(m => m.Code == "L1-SMT-01", ct);
        var station = await db.Stations.SingleOrDefaultAsync(s => s.Code == "L1-SMT-01", ct);
        var item = await db.Items.SingleOrDefaultAsync(i => i.Code == "PCB-A", ct);
        if (machine is null || station is null || item is null) return;
        DateTime At(int h, int m, int s = 0) => TimeZoneInfo.ConvertTimeToUtc(demo.Date.ToDateTime(new TimeOnly(h, m, s)), plant.Zone);

        if (!await db.WorkOrders.AnyAsync(w => w.WoNo == demo.WoNo, ct))
        {
            // The machine already has a history on that day (a database used for something else): leave it alone.
            var (shiftStart, shiftEnd) = (At(6, 0), At(14, 0));
            if (await db.MachineStateHistory.AnyAsync(x => x.MachineId == machine.Id && x.StartAt < shiftEnd && (x.EndAt == null || x.EndAt > shiftStart), ct))
                return;
            await WriteRowsAsync(demo, machine.Id, machine.WorkCenterId, station, item.Id, At, ct);
        }

        // Close the hours and the shift as the job would have at 14:05 that day. Every start, not only after writing
        // the rows: a start that stopped in between (it happened) would otherwise leave the shift without snapshots,
        // older than the job's catch-up range. Rows already final are skipped, so a normal start writes nothing.
        var time = new FixedTime(At(14, 5));
        var options = Options.Create(new OeeOptions { CatchUpDays = 0 });
        await new OeeSnapshotService(db, new OeeInputReader(db, time, options), plant, time, options, notifier,
            logs.CreateLogger<OeeSnapshotService>()).CloseDueBucketsAsync(ct, [machine.Id]);
    }

    private async Task WriteRowsAsync(DemoShift demo, int machineId, int workCenterId, Station station, int itemId,
        Func<int, int, int, DateTime> plantTime, CancellationToken ct)
    {
        DateTime At(int h, int m, int s = 0) => plantTime(h, m, s);

        // A WO whose SMT step has the ideal cycle time of the example: 20 s (the routing master says 30 s).
        var wo = WorkOrder.Create(demo.WoNo, itemId, station.LineId, demo.Boards, At(6, 0), At(14, 0), requireFai: false);
        wo.Release(1, 1, [new MaterialSnapshot(itemId, 10, 1m)],
            [new RoutingStepSnapshot(10, "SMT", workCenterId, 20m, false, true, null)], At(5, 0));
        // History only: a released order would sit on the dispatch list, and the simulator's station would try to
        // make its boards (whose serials carry no check digit) in the step 9 live demo.
        wo.Start(At(6, 16));
        wo.Complete("Demo shift (OEE history only)", At(14, 0));
        wo.Close();
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync(ct);
        var boards = Enumerable.Range(1, demo.Boards).Select(i => new Serial($"{demo.SerialPrefix}{i:D5}", wo.Id, itemId)).ToList();
        db.Serials.AddRange(boards);
        await db.SaveChangesAsync(ct);

        // 15 min meeting, FDR-JAM 25 min, MAT-WAIT 12 min, an 8-minute QC wait nobody classified, six 20 s jams,
        // Idle through both breaks: 405 planned, 360 run.
        var reasons = await db.DowntimeReasons.ToDictionaryAsync(r => r.Code, r => r.Id, ct);
        (DateTime Start, MachineState State, string? Reason)[] segments =
        [
            (At(6, 0), MachineState.PlannedStop, "MEETING"), (At(6, 15), MachineState.Running, null),
            (At(7, 10), MachineState.Down, "FDR-JAM"), (At(7, 35), MachineState.Running, null),
            .. Enumerable.Range(0, 6).SelectMany(i => new (DateTime, MachineState, string?)[]
                { (At(7, 50 + i), MachineState.Down, null), (At(7, 50 + i, 20), MachineState.Running, null) }),
            (At(9, 20), MachineState.Down, "MAT-WAIT"), (At(9, 32), MachineState.Running, null),
            (At(10, 0), MachineState.Idle, null), (At(10, 15), MachineState.Running, null),
            (At(11, 30), MachineState.Idle, null), (At(12, 15), MachineState.Running, null),
            (At(12, 40), MachineState.Down, null), (At(12, 48), MachineState.Running, null),
        ];
        for (var i = 0; i < segments.Length; i++)
        {
            var (start, state, reason) = segments[i];
            var h = new MachineStateHistory(machineId, state, start);
            if (reason is not null && reasons.TryGetValue(reason, out var id)) h.AssignReason(id);
            h.Close(i < segments.Length - 1 ? segments[i + 1].Start : At(14, 0));   // closed: no open segment left behind
            db.MachineStateHistory.Add(h);
        }

        // Board n is finished after n cycles of Running time (half a cycle earlier, so the last one is inside the
        // half-open window): an hour with a stop makes fewer boards, and P per hour stays honest.
        var running = segments.Select((x, i) => (x.State, Start: x.Start, End: i < segments.Length - 1 ? segments[i + 1].Start : At(14, 0)))
            .Where(x => x.State == MachineState.Running).Select(x => (x.Start, x.End)).ToList();
        var cycle = running.Sum(r => (r.End - r.Start).TotalSeconds) / demo.Boards;
        DateTime BoardAt(int n)
        {
            var offset = (n - 0.5) * cycle;
            foreach (var (start, end) in running)
            {
                var length = (end - start).TotalSeconds;
                if (offset < length) return start.AddSeconds(offset);
                offset -= length;
            }
            return running[^1].End.AddSeconds(-1);
        }
        DateTime MessageAt(int board) => BoardAt((board / 5 + 1) * 5);   // 0-based board -> its counter message

        // A baseline before the shift, then +5 per message (CounterMath as in the ingest).
        long? last = null;
        foreach (var (at, counter) in new[] { (At(5, 55), 50_000L) }
                     .Concat(Enumerable.Range(1, demo.Boards / 5).Select(i => (BoardAt(5 * i), 50_000L + 5 * i))))
        {
            db.MachineEvents.Add(MachineEvent.Count(Guid.NewGuid(), machineId, counter, CounterMath.Compute(last, counter), null, null, at, at));
            last = counter;
        }

        // OK / NG scans by serial (the source of Good, spec 1.2.10), each at the time its board was counted: an hour
        // then has as many scans as counts, and only a real gap shows up in the cross-check.
        var stride = demo.Scans / demo.Ng;
        for (var j = 0; j < demo.Scans; j++)
        {
            var ng = j % stride == stride - 1 && j / stride < demo.Ng;
            db.ScanLogs.Add(new ScanLog(boards[j], station.Id, 10, ng ? ScanResult.Ng : ScanResult.Ok, ng ? "SLD-BRG" : null,
                "EMP00123", ScanSource.Machine, MessageAt(j)));
        }
        await db.SaveChangesAsync(ct);
    }
}
