using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Machines;
using MiniMes.Domain.MasterData.Boms;
using MiniMes.Domain.MasterData.Calendar;
using MiniMes.Domain.MasterData.DefectCodes;
using MiniMes.Domain.MasterData.Downtime;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Domain.MasterData.Items;
using MiniMes.Domain.MasterData.Routings;

namespace MiniMes.Infrastructure.Persistence.Seed;

// M1 demo data (docs/erd-master-data.md section 3): PCB-A with a two-level BOM, its routing, Line 1 equipment,
// shifts, the plant calendar and the defect/downtime catalogs. Runs after DevSeeder (needs Line 1 and its stations).
// Idempotent per record: anything whose code already exists is left as it is, so running it twice (or on a DB
// where someone already typed part of this data) changes nothing. Goes through the domain methods, not HasData,
// so BOM/routing approval follows the same rules as the API.
public class MasterDataSeeder(AppDbContext db, TimeProvider time)
{
    public const string SeedLineCode = "L1";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var line = await db.Lines.FirstOrDefaultAsync(l => l.Code == SeedLineCode, ct);
        if (line is null) return; // factory not seeded (DevSeeder skipped because sites already existed)

        // Valid from the start of the seed day (UTC): an explode right after seeding already sees it.
        var effectiveFrom = time.GetUtcNow().UtcDateTime.Date;

        await SeedItemsAsync(ct);
        var workCenters = await SeedEquipmentAsync(line.Id, ct);
        await SeedBomsAsync(effectiveFrom, ct);
        await SeedRoutingAsync(workCenters, effectiveFrom, ct);
        await SeedShiftsAsync(ct);
        await SeedCalendarAsync(DateOnly.FromDateTime(effectiveFrom).Year, ct);
        await SeedCatalogsAsync(ct);
        await SeedMachineTagsAsync(ct);
    }

    // M4 2.3b: process tags the ingest accepts. Plausible = the physical range (a cut sensor wire reads -32768),
    // not the M7 specification limits.
    private static readonly (string Machine, string Tag, string Unit, double Lower, double Upper)[] MachineTags =
    [
        ("L1-REFLOW-01", "peakTemp", "°C", 0, 400),
    ];

    private async Task SeedMachineTagsAsync(CancellationToken ct)
    {
        var machines = await db.Machines.ToDictionaryAsync(m => m.Code, m => m.Id, ct);
        var existing = await db.MachineTags.Select(t => new { t.MachineId, t.TagName }).ToListAsync(ct);
        foreach (var t in MachineTags.Where(t => machines.ContainsKey(t.Machine)))
            if (!existing.Any(e => e.MachineId == machines[t.Machine] && e.TagName == t.Tag))
                db.MachineTags.Add(new MachineTag(machines[t.Machine], t.Tag, t.Unit, t.Lower, t.Upper));
        await db.SaveChangesAsync(ct);
    }

    private static readonly (string Code, string Name, ItemType Type, string Uom, bool SkipIqc)[] Items =
    [
        ("PCB-A", "Controller board A", ItemType.Finished, Units.Pieces, false),
        ("PCBA-SMT", "PCB-A after SMT", ItemType.Semi, Units.Pieces, false),
        ("PCB-BARE", "Bare PCB for PCB-A", ItemType.Raw, Units.Pieces, false),
        ("RES-10K-0402", "Resistor 10k 0402", ItemType.Raw, Units.Pieces, false),
        ("CAP-100N-0402", "Capacitor 100nF 0402", ItemType.Raw, Units.Pieces, false),
        ("IC-MCU-QFN", "MCU QFN", ItemType.Raw, Units.Pieces, false),
        ("SOLDER-PASTE", "Solder paste", ItemType.Raw, Units.Kilogram, false),
        ("CONN-USBC", "USB-C connector", ItemType.Raw, Units.Pieces, false),
        ("SHIELD-CAN", "EMI shield can", ItemType.Raw, Units.Pieces, false),
        ("PACK-BAG", "ESD packing bag", ItemType.Raw, Units.Pieces, true), // bags are not inspected at IQC (M6)
    ];

    private async Task SeedItemsAsync(CancellationToken ct)
    {
        var existing = await db.Items.Select(i => i.Code).ToListAsync(ct);
        foreach (var (code, name, type, uom, skipIqc) in Items.Where(i => !existing.Contains(i.Code)))
        {
            var (serialized, lotControlled) = Item.ExpectedTraceability(type);
            db.Items.Add(Item.Create(code, name, type, uom, serialized, lotControlled, skipIqc));
        }
        await db.SaveChangesAsync(ct); // BOM lines need the item ids
    }

    // Work center -> its machines (MqttTopicKey = machine code) and the M0 station that scans there.
    private static readonly (string Code, string Name, string Type, string Station,
        (string Code, string Name, string Type, MachineDataSource Source)[] Machines)[] WorkCenters =
    [
        ("WC-SMT", "SMT line 1", "SMT", "L1-SMT-01",
            [("L1-SMT-01", "Pick and place 1", "PickAndPlace", MachineDataSource.Mqtt),
             ("L1-REFLOW-01", "Reflow oven 1", "ReflowOven", MachineDataSource.OpcUa)]),
        ("WC-ASSY", "Assembly line 1", "ASSY", "L1-ASM-01",
            [("L1-GLUE-01", "Glue dispenser 1", "GlueDispenser", MachineDataSource.Modbus)]),
        ("WC-TEST", "Test line 1", "TEST", "L1-TEST-01",
            [("L1-TEST-01", "Functional tester 1", "FunctionalTester", MachineDataSource.Mqtt)]),
        ("WC-PACK", "Packing line 1", "PACK", "L1-PACK-01", []),
    ];

    private async Task<Dictionary<string, int>> SeedEquipmentAsync(int lineId, CancellationToken ct)
    {
        var existingWc = await db.WorkCenters.Select(w => w.Code).ToListAsync(ct);
        foreach (var wc in WorkCenters.Where(w => !existingWc.Contains(w.Code)))
            db.WorkCenters.Add(new WorkCenter(lineId, wc.Code, wc.Name, wc.Type));
        await db.SaveChangesAsync(ct);

        var wcs = await db.WorkCenters.Where(w => WorkCenters.Select(x => x.Code).Contains(w.Code))
            .ToDictionaryAsync(w => w.Code, ct);
        var existingMachines = await db.Machines.Select(m => m.Code).ToListAsync(ct);
        var stations = await db.Stations.Where(s => s.LineId == lineId).ToListAsync(ct);

        foreach (var wc in WorkCenters)
        {
            var workCenter = wcs[wc.Code];
            foreach (var m in wc.Machines.Where(m => !existingMachines.Contains(m.Code)))
                db.Machines.Add(new Machine(workCenter.Id, m.Code, m.Name, m.Type, m.Code, m.Source));

            // Only stations nobody has linked yet; L1-REPAIR stays without a work center (not in the routing).
            var station = stations.FirstOrDefault(s => s.Code == wc.Station);
            if (station is { WorkCenterId: null })
                station.AssignWorkCenter(workCenter.Id, workCenter.LineId);
        }
        await db.SaveChangesAsync(ct);
        return wcs.ToDictionary(x => x.Key, x => x.Value.Id);
    }

    // OperationSeq of the PCBA-SMT lines uses the PCB-A routing numbers (10 = SMT): MVP has one routing (spec 1.2.4).
    private static readonly (string Parent, (string Component, decimal QtyPer, decimal Scrap, int Seq)[] Lines)[] Boms =
    [
        ("PCBA-SMT",
        [
            ("PCB-BARE", 1m, 0.005m, 10), ("RES-10K-0402", 12m, 0.02m, 10), ("CAP-100N-0402", 20m, 0.02m, 10),
            ("IC-MCU-QFN", 1m, 0.005m, 10), ("SOLDER-PASTE", 0.0006m, 0.05m, 10),
        ]),
        ("PCB-A",
        [
            ("PCBA-SMT", 1m, 0.01m, 20), ("CONN-USBC", 1m, 0.005m, 20), ("SHIELD-CAN", 1m, 0.005m, 20), ("PACK-BAG", 1m, 0m, 40),
        ]),
    ];

    private async Task SeedBomsAsync(DateTime effectiveFrom, CancellationToken ct)
    {
        var ids = await ItemIdsAsync(ct);
        foreach (var (parent, lines) in Boms)
        {
            // Any version at all (even a Draft) means someone owns this BOM now: do not add a v1 next to it.
            if (await db.Boms.AnyAsync(b => b.ItemId == ids[parent], ct)) continue;

            var bom = Bom.CreateDraft(ids[parent], version: 1);
            foreach (var (component, qtyPer, scrap, seq) in lines)
                bom.AddLine(ids[component], qtyPer, scrap, seq);
            bom.Approve(effectiveFrom, previous: null);
            db.Boms.Add(bom);
        }
        await db.SaveChangesAsync(ct);
    }

    private static readonly (int Seq, string Operation, string WorkCenter, decimal CycleSec, bool Inspection)[] Steps =
    [
        (10, "SMT", "WC-SMT", 30m, false),
        (20, "ASSY", "WC-ASSY", 60m, false),
        (30, "TEST", "WC-TEST", 45m, true),
        (40, "PACK", "WC-PACK", 20m, false),
    ];

    private async Task SeedRoutingAsync(Dictionary<string, int> workCenters, DateTime effectiveFrom, CancellationToken ct)
    {
        var pcbA = (await ItemIdsAsync(ct))["PCB-A"];
        if (await db.Routings.AnyAsync(r => r.ItemId == pcbA, ct)) return;

        var routing = Routing.CreateDraft(pcbA, version: 1);
        foreach (var (seq, operation, wc, cycleSec, inspection) in Steps)
            // Files are in docs/sop/PCB-A/ (made by docs/sop/make-sop.cs); the kiosk opens them in M3.
            routing.AddStep(seq, operation, workCenters[wc], cycleSec, inspection, isMandatory: true,
                sopUrl: $"/sop/PCB-A/{operation}-v1.pdf");
        routing.Approve(effectiveFrom, previous: null);
        db.Routings.Add(routing);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedShiftsAsync(CancellationToken ct)
    {
        static TimeOnly T(int h, int m = 0) => new(h, m);
        Shift[] shifts =
        [
            new("S1", "Morning", T(6), T(14), WorkDays.MonToSat, [(T(10), T(10, 15)), (T(11, 30), T(12, 15))]),
            new("S2", "Afternoon", T(14), T(22), WorkDays.MonToSat, [(T(18), T(18, 45)), (T(20), T(20, 15))]),
            // Night shift crosses midnight; Friday's S3 belongs to production date Friday (spec 1.2.8).
            new("S3", "Night", T(22), T(6), WorkDays.MonToFri, [(T(1), T(1, 45)), (T(3, 30), T(3, 45))]),
        ];
        var existing = await db.Shifts.Select(s => s.Code).ToListAsync(ct);
        db.Shifts.AddRange(shifts.Where(s => !existing.Contains(s.Code)));
        await db.SaveChangesAsync(ct);
    }

    // Public holidays of the seed year + one Sunday overtime with S1 only. Dates that already have a row
    // (somebody planned them by hand) are kept.
    private async Task SeedCalendarAsync(int year, CancellationToken ct)
    {
        var days = VietnamHolidays.For(year)
            .Select(h => new PlantCalendarDay(h.Date, isWorkingDay: false, shiftCodes: null, h.Name))
            .Append(new PlantCalendarDay(FirstSunday(year, 12), isWorkingDay: true, shiftCodes: "S1",
                "Overtime for the year-end order"))
            .ToList();
        var dates = days.Select(d => d.Date).ToList();
        var existing = await db.PlantCalendar.Where(d => dates.Contains(d.Date)).Select(d => d.Date).ToListAsync(ct);
        db.PlantCalendar.AddRange(days.Where(d => !existing.Contains(d.Date)));
        await db.SaveChangesAsync(ct);
    }

    private static DateOnly FirstSunday(int year, int month)
    {
        var d = new DateOnly(year, month, 1);
        return d.AddDays(((int)DayOfWeek.Sunday - (int)d.DayOfWeek + 7) % 7);
    }

    private static readonly (string Code, string Name, string Category)[] DefectCodes =
    [
        ("SLD-BRG", "Solder bridge", "Solder"),
        ("SLD-OPN", "Open solder joint", "Solder"),
        ("SLD-INS", "Insufficient solder", "Solder"),
        ("SLD-BALL", "Solder ball", "Solder"),
        ("CMP-MIS", "Missing component", "Component"),
        ("CMP-POL", "Wrong polarity", "Component"),
        ("CMP-SHF", "Shifted component", "Component"),
        ("CMP-WRG", "Wrong component", "Component"),
        ("FUN-NOPWR", "No power on test", "Functional"),
        ("COS-SCR", "Scratch on board", "Cosmetic"),
    ];

    // CountsAgainstAvailability false = excluded from planned production time; true = an Availability loss (M5).
    private static readonly (string Code, string Name, DowntimeCategory Category, bool Counts)[] DowntimeReasons =
    [
        ("PM", "Preventive maintenance", DowntimeCategory.Planned, false),
        ("NOPLAN", "No production plan", DowntimeCategory.Planned, false),
        ("MEETING", "Team meeting", DowntimeCategory.Planned, false),
        ("CHG-MODEL", "Model changeover", DowntimeCategory.Planned, true), // counted so it can be measured and cut
        ("MCH-FAULT", "Machine fault", DowntimeCategory.Unplanned, true),
        ("MAT-WAIT", "Waiting for material", DowntimeCategory.Unplanned, true),
        ("FDR-JAM", "Feeder jam", DowntimeCategory.Unplanned, true),
        ("QC-WAIT", "Waiting for QC approval", DowntimeCategory.Unplanned, true),
        ("NO-OPR", "No operator", DowntimeCategory.Unplanned, true),
    ];

    private async Task SeedCatalogsAsync(CancellationToken ct)
    {
        var defects = await db.DefectCodes.Select(d => d.Code).ToListAsync(ct);
        db.DefectCodes.AddRange(DefectCodes.Where(d => !defects.Contains(d.Code))
            .Select(d => new DefectCode(d.Code, d.Name, d.Category)));

        var reasons = await db.DowntimeReasons.Select(r => r.Code).ToListAsync(ct);
        db.DowntimeReasons.AddRange(DowntimeReasons.Where(r => !reasons.Contains(r.Code))
            .Select(r => new DowntimeReason(r.Code, r.Name, r.Category, r.Counts)));
        await db.SaveChangesAsync(ct);
    }

    private Task<Dictionary<string, int>> ItemIdsAsync(CancellationToken ct)
    {
        var codes = Items.Select(i => i.Code).ToList();
        return db.Items.Where(i => codes.Contains(i.Code)).ToDictionaryAsync(i => i.Code, i => i.Id, ct);
    }
}
