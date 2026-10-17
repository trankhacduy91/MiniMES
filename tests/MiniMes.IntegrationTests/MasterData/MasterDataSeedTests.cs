using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using MiniMes.Contracts.Audit;
using MiniMes.Contracts.Common;
using MiniMes.Contracts.MasterData;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.MasterData.Equipment;
using MiniMes.Infrastructure.Persistence;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.MasterData;

// Spec M1 step 9 / DoD 5.1: the Testing host seeds like Development, so this checks the data a fresh clone gets.
// Other tests never use the plain seed codes (they add a unique suffix), so the seeded rows stay as seeded.
[Collection(ApiCollection.Name)]
public class MasterDataSeedTests(ApiFixture api)
{
    [Fact]
    public async Task Seeded_pcb_a_times_200_gives_the_dod_table()
    {
        var admin = await api.LoginAsync("admin");
        var pcbA = await api.WithDbAsync(db => db.Items.SingleAsync(i => i.Code == "PCB-A"));

        var result = await admin.GetFromJsonAsync<BomExplosionDto>($"/api/items/{pcbA.Id}/explosion?qty=200");

        var required = result!.Lines.ToDictionary(l => l.Code, l => l.RequiredQty);
        Assert.Equal(new Dictionary<string, decimal>
        {
            ["PCBA-SMT"] = 202, ["PCB-BARE"] = 204, ["RES-10K-0402"] = 2473, ["CAP-100N-0402"] = 4121, ["IC-MCU-QFN"] = 204,
            ["SOLDER-PASTE"] = 0.12726m, ["CONN-USBC"] = 201, ["SHIELD-CAN"] = 201, ["PACK-BAG"] = 200,
        }, required);
        Assert.Equal(0.127m, Math.Round(required["SOLDER-PASTE"], 3)); // kg, shown with 3 decimals
    }

    [Fact]
    public async Task Running_the_seeder_twice_more_changes_nothing()
    {
        var before = await api.WithDbAsync(CountsAsync);

        await api.WithDbAsync(async db => { await new MasterDataSeeder(db, TimeProvider.System).SeedAsync(); return 0; });
        await api.WithDbAsync(async db => { await new MasterDataSeeder(db, TimeProvider.System).SeedAsync(); return 0; });

        Assert.Equal(before, await api.WithDbAsync(CountsAsync));
    }

    [Fact]
    public async Task Boms_and_routing_are_approved_v1_with_a_sop_for_every_step()
    {
        var (boms, routing) = await api.WithDbAsync(async db =>
        {
            var ids = await db.Items.Where(i => i.Code == "PCB-A" || i.Code == "PCBA-SMT").ToDictionaryAsync(i => i.Id, i => i.Code);
            var b = await db.Boms.Where(x => ids.Keys.Contains(x.ItemId)).ToListAsync();
            var r = await db.Routings.Include(x => x.Steps).SingleAsync(x => ids.Keys.Contains(x.ItemId));
            return (b, r);
        });

        Assert.Equal(2, boms.Count);
        Assert.All(boms, b => Assert.Equal((1, VersionStatus.Approved, (DateTime?)null), (b.Version, b.Status, b.EffectiveTo)));
        Assert.Equal(VersionStatus.Approved, routing.Status);
        Assert.Equal(["SMT", "ASSY", "TEST", "PACK"], routing.Steps.OrderBy(s => s.Seq).Select(s => s.OperationCode));
        Assert.All(routing.Steps, s => Assert.Equal($"/sop/PCB-A/{s.OperationCode}-v1.pdf", s.SopUrl));
        Assert.All(routing.Steps, s => Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs", s.SopUrl!.TrimStart('/')))));
    }

    [Fact]
    public async Task Line_1_has_the_v2_machines_and_its_routing_stations_point_at_work_centers()
    {
        var (machines, stations) = await api.WithDbAsync(async db => (
            await db.Machines.Where(m => m.Code.StartsWith("L1-")).ToDictionaryAsync(m => m.Code, m => m.DataSource),
            await db.Stations.Where(s => s.Code.StartsWith("L1-"))
                .Select(s => new { s.Code, WorkCenter = db.WorkCenters.Where(w => w.Id == s.WorkCenterId).Select(w => w.Code).FirstOrDefault() })
                .ToDictionaryAsync(s => s.Code, s => s.WorkCenter)));

        Assert.Equal(MachineDataSource.Mqtt, machines["L1-SMT-01"]);
        Assert.Equal(MachineDataSource.OpcUa, machines["L1-REFLOW-01"]);
        Assert.Equal(MachineDataSource.Modbus, machines["L1-GLUE-01"]);
        Assert.Equal(MachineDataSource.Mqtt, machines["L1-TEST-01"]);
        Assert.Equal("WC-SMT", stations["L1-SMT-01"]);
        Assert.Equal("WC-ASSY", stations["L1-ASM-01"]);
        Assert.Equal("WC-TEST", stations["L1-TEST-01"]);
        Assert.Equal("WC-PACK", stations["L1-PACK-01"]);
        Assert.Null(stations["L1-REPAIR"]); // not a routing step
    }

    [Fact]
    public async Task Shifts_catalogs_calendar_and_skip_iqc_are_seeded()
    {
        var year = DateTime.UtcNow.Year;
        var data = await api.WithDbAsync(async db => new
        {
            Shifts = await db.Shifts.Include(s => s.Breaks).Where(s => s.Code == "S1" || s.Code == "S2" || s.Code == "S3").ToListAsync(),
            Defects = await db.DefectCodes.CountAsync(d => SeedDefects.Contains(d.Code)),
            Calendar = await db.PlantCalendar.Where(d => d.Date.Year == year).ToListAsync(),
            SkipIqc = await db.Items.Where(i => i.SkipIqc && SeedItems.Contains(i.Code)).Select(i => i.Code).ToListAsync(),
            Reasons = await db.DowntimeReasons.Select(r => r.Code).ToListAsync(),
        });

        Assert.All(data.Shifts, s => Assert.Equal(420, s.PlannedProductionMinutes));
        Assert.True(Assert.Single(data.Shifts, s => s.Code == "S3").CrossesMidnight);
        Assert.Equal(SeedDefects.Length, data.Defects);
        Assert.Subset(data.Reasons.ToHashSet(), new HashSet<string> { "PM", "NOPLAN", "CHG-MODEL", "MCH-FAULT", "MAT-WAIT", "FDR-JAM" });
        Assert.Contains(data.Calendar, d => !d.IsWorkingDay && d.Date == new DateOnly(year, 9, 2));
        Assert.Equal(5, data.Calendar.Count(d => d.Note == "Lunar New Year (Tet)"));
        var overtime = Assert.Single(data.Calendar, d => d.IsWorkingDay);
        Assert.Equal((DayOfWeek.Sunday, "S1"), (overtime.Date.DayOfWeek, overtime.ShiftCodes));
        Assert.Equal(["PACK-BAG"], data.SkipIqc);
    }

    // DoD: master changes are audited; checked once on an Approve made through the API.
    [Fact]
    public async Task Approving_a_bom_writes_audit_rows_by_the_user()
    {
        var admin = await api.LoginAsync("admin");
        var (bom, _) = await BomVersionApiTests.ApprovedBomAsync(admin);

        var audit = await admin.GetFromJsonAsync<PagedResult<AuditLogDto>>($"/api/audit-logs?tableName=Boms&recordKey={bom.Id}");

        var status = Assert.Single(audit!.Items, r => r.ColumnName == "Status");
        Assert.Equal(("Draft", "Approved", "admin"), (status.OldValue, status.NewValue, status.ChangedBy));
        Assert.Contains(audit.Items, r => r.ColumnName == "EffectiveFrom");
    }

    private static readonly string[] SeedItems =
        ["PCB-A", "PCBA-SMT", "PCB-BARE", "RES-10K-0402", "CAP-100N-0402", "IC-MCU-QFN", "SOLDER-PASTE", "CONN-USBC", "SHIELD-CAN", "PACK-BAG"];

    private static readonly string[] SeedDefects =
        ["SLD-BRG", "SLD-OPN", "SLD-INS", "SLD-BALL", "CMP-MIS", "CMP-POL", "CMP-SHF", "CMP-WRG", "FUN-NOPWR", "COS-SCR"];

    private static async Task<int[]> CountsAsync(AppDbContext db) =>
    [
        await db.Items.CountAsync(), await db.Boms.CountAsync(), await db.Routings.CountAsync(),
        await db.WorkCenters.CountAsync(), await db.Machines.CountAsync(), await db.Shifts.CountAsync(),
        await db.PlantCalendar.CountAsync(), await db.DefectCodes.CountAsync(), await db.DowntimeReasons.CountAsync(),
        await db.AuditLogs.CountAsync(), // nothing saved at all, not even an audit row
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiniMes.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo root not found.");
    }
}
