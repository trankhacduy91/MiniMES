using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Common;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Fai;
using MiniMes.Infrastructure.Persistence;
using MiniMes.Infrastructure.Persistence.Seed;
using MiniMes.IntegrationTests.Infrastructure;

namespace MiniMes.IntegrationTests.Quality;

// Spec M7 step 2 check: the seed, and the rules the database enforces by itself (docs/quality-erd.md section 5).
// Each test makes its own plan or serial, so rows of other tests never collide with its own.
[Collection(ApiCollection.Name)]
public class QualitySchemaTests(ApiFixture api)
{
    private static readonly DateTime T0 = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);
    private static int _nextSerial;

    [Fact]
    public async Task Seed_has_the_two_plans_their_baselines_the_reflow_link_and_the_fai_checklist()
    {
        var pcbA = await api.WithDbAsync(db => db.Items.Where(i => i.Code == "PCB-A").Select(i => i.Id).SingleAsync());
        var seed = await api.WithDbAsync(async db => new
        {
            Rail = await db.InspectionPlans.SingleAsync(p => p.Code == QualitySeeder.RailPlan),
            Reflow = await db.InspectionPlans.SingleAsync(p => p.Code == QualitySeeder.ReflowPlan),
            Limits = await db.ControlLimitSets.Where(c => c.IsActive).ToListAsync(),
            Tag = await ReflowTagAsync(db),
            Fai = await db.FaiTemplates.SingleAsync(t => t.IsActive && t.ItemId == pcbA),
        });

        Assert.Equal((3.135m, 3.465m, "TEST"), (seed.Rail.Lsl, seed.Rail.Usl, seed.Rail.OperationCode));
        Assert.NotNull(seed.Rail.DefectCodeId);
        Assert.Equal((235m, 255m, (int?)null), (seed.Reflow.Lsl, seed.Reflow.Usl, seed.Reflow.ItemId));
        // Spec 1.2: UCL = 3.302 + 3 × 0.040 = 3.422, LCL = 3.182 (the 3.44 V example sits above it, inside USL).
        var rail = seed.Limits.Single(c => c.InspectionPlanId == seed.Rail.Id);
        Assert.Equal((3.422m, 3.182m), (rail.Ucl, rail.Lcl));
        var reflow = seed.Limits.Single(c => c.InspectionPlanId == seed.Reflow.Id);
        Assert.Equal((247.4m, 242.6m), (reflow.Ucl, reflow.Lcl));
        Assert.Equal((seed.Reflow.Id, 30), (seed.Tag.SpcCharacteristicId, seed.Tag.SpcSampleEverySec));
        Assert.Equal(1, seed.Fai.Version);
        Assert.Equal(["BomMatch", "TestPassed", "MeasurementsInSpec"],
            seed.Fai.Items().Where(i => i.Kind == FaiCheckKind.Auto).Select(i => i.Code));
    }

    [Fact]
    public async Task Ncr_numbers_come_from_a_sequence()
    {
        var (a, b) = await api.WithDbAsync(async db => (await NextNcrAsync(db), await NextNcrAsync(db)));
        Assert.True(b > a);
        Assert.Matches(@"^NCR-\d{6}$", Ncr.FormatNo(a));
    }

    // Q16: a measurement is evidence; a wrong value gets a correction row, the old row never changes.
    [Fact]
    public async Task Measurements_are_insert_only()
    {
        var s = await SerialAsync();
        var id = await AddAsync(Measurement.OfSerial(await RailAsync(), MeasurementSource.Station, s.SerialId, s.StationId, 3.30m, "EMP00123", T0));

        foreach (var sql in new FormattableString[] { $"UPDATE Measurements SET Value = 3.31 WHERE Id = {id}", $"DELETE FROM Measurements WHERE Id = {id}" })
            Assert.Contains("append-only", (await Assert.ThrowsAsync<SqlException>(() => SqlAsync(sql))).Message);
    }

    // Q1 + step 1 decision 2: the server judges against the plan, and the row keeps the limits it used.
    [Fact]
    public async Task A_measurement_keeps_the_result_and_the_limits_it_was_judged_against()
    {
        var s = await SerialAsync();
        var plan = await RailAsync();
        var ok = await AddAsync(Measurement.OfSerial(plan, MeasurementSource.Station, s.SerialId, s.StationId, 3.440m, "EMP00123", T0));
        var ng = await AddAsync(Measurement.OfSerial(plan, MeasurementSource.Station, s.SerialId, s.StationId, 3.470m, "EMP00123", T0.AddSeconds(1)));

        var rows = await api.WithDbAsync(db => db.Measurements.Where(m => m.Id == ok || m.Id == ng).OrderBy(m => m.Id)
            .Select(m => new { m.Result, m.Usl, m.Lsl }).ToListAsync());
        Assert.Equal([(MeasureResult.Ok, 3.465m), (MeasureResult.Ng, 3.465m)], rows.Select(r => (r.Result, r.Usl!.Value)));
    }

    // 2.3 trap 3 and 2.3b: a resent request and a sampler running twice each store one point.
    [Fact]
    public async Task A_resent_request_and_a_repeated_machine_sample_are_stored_once()
    {
        var s = await SerialAsync();
        var plan = await RailAsync();
        var request = Guid.NewGuid();
        await AddAsync(Measurement.OfSerial(plan, MeasurementSource.Station, s.SerialId, s.StationId, 3.30m, "EMP00123", T0, request));
        await Assert.ThrowsAsync<ConflictException>(() =>
            AddAsync(Measurement.OfSerial(plan, MeasurementSource.Station, s.SerialId, s.StationId, 3.30m, "EMP00123", T0, request)));

        var (reflow, tagId) = await api.WithDbAsync(async db => (
            await db.InspectionPlans.SingleAsync(p => p.Code == QualitySeeder.ReflowPlan),
            (await ReflowTagAsync(db)).Id));
        var sourceTs = T0.AddMilliseconds(Random.Shared.Next(1, 1_000_000_000));   // other tests sample this tag too
        await AddAsync(Measurement.FromMachine(reflow, tagId, 245.1m, sourceTs));
        await Assert.ThrowsAsync<ConflictException>(() => AddAsync(Measurement.FromMachine(reflow, tagId, 245.1m, sourceTs)));
    }

    // Q18: a machine sample has no board; a board measurement has no tag. Raw SQL cannot mix them either.
    [Fact]
    public async Task Database_refuses_a_measurement_with_two_sources()
    {
        var s = await SerialAsync();
        var (plan, tagId) = await api.WithDbAsync(async db => (
            (await RailAsync()).Id, (await ReflowTagAsync(db)).Id));

        var ex = await Assert.ThrowsAsync<SqlException>(() => SqlAsync($"""
            INSERT INTO Measurements (InspectionPlanId, Source, SerialId, StationId, MachineTagId, Value, Result, MeasuredAt, SourceTs)
            VALUES ({plan}, 'Machine', {s.SerialId}, {s.StationId}, {tagId}, 3.3, 'Ok', {T0}, {T0})
            """));
        Assert.Contains("CK_Measurements_OneSource", ex.Message);
    }

    // Q5: one Open alert per (plan, rule); once closed, the rule may raise a new one.
    [Fact]
    public async Task One_open_alert_per_plan_and_rule()
    {
        var plan = await PlanAsync();
        var s = await SerialAsync();
        var point = await AddAsync(Measurement.OfSerial(plan, MeasurementSource.Station, s.SerialId, s.StationId, 3.5m, "EMP00123", T0));
        var first = new SpcAlert(plan.Id, SpcRuleCode.WE1, point, T0);
        await AddAsync(first);

        await Assert.ThrowsAsync<ConflictException>(() => AddAsync(new SpcAlert(plan.Id, SpcRuleCode.WE1, point, T0)));
        await AddAsync(new SpcAlert(plan.Id, SpcRuleCode.WE2, point, T0));                  // another rule: its own alert

        await api.WithDbAsync(async db =>
        {
            (await db.SpcAlerts.SingleAsync(a => a.Id == first.Id)).Close("qc01", "Probe re-seated", T0.AddMinutes(5));
            return await db.SaveChangesAsync();
        });
        await AddAsync(new SpcAlert(plan.Id, SpcRuleCode.WE1, point, T0.AddMinutes(6)));
    }

    // Q3: one baseline in use per plan.
    [Fact]
    public async Task One_active_baseline_per_plan()
    {
        var plan = await PlanAsync();
        ControlLimitSet Baseline() => new(plan.Id, 3.3m, 0.04m, null, 25, null, null, "Test", "qc01", T0);
        var first = Baseline();
        await AddAsync(first);

        await Assert.ThrowsAsync<ConflictException>(() => AddAsync(Baseline()));

        await api.WithDbAsync(async db =>
        {
            (await db.ControlLimitSets.SingleAsync(c => c.Id == first.Id)).Deactivate();
            db.ControlLimitSets.Add(Baseline());
            return await db.SaveChangesAsync();
        });
        Assert.Equal(2, await api.WithDbAsync(db => db.ControlLimitSets.CountAsync(c => c.InspectionPlanId == plan.Id)));
    }

    // 2.6: a hold is history. Released once, linked to an NCR later; never edited otherwise or deleted.
    [Fact]
    public async Task A_hold_record_only_gets_its_release_and_its_ncr()
    {
        var s = await SerialAsync();
        var hold = new HoldRecord(HoldTargetType.Serial, s.SerialId, Guid.NewGuid(), HoldScope.Serials, null, "Suspect solder paste", null, "qc01", T0);
        await AddAsync(hold);
        var ncr = await api.WithDbAsync(async db =>
        {
            var n = new Ncr(Ncr.FormatNo(await NextNcrAsync(db)), NcrSource.Manual, null, null, "Suspect solder paste", 1,
                [(HoldTargetType.Serial, s.SerialId)]);
            db.Ncrs.Add(n);
            await db.SaveChangesAsync();
            return n.Id;
        });

        await SqlAsync($"UPDATE HoldRecords SET NcrId = {ncr} WHERE Id = {hold.Id}");
        foreach (var (sql, message) in new (FormattableString, string)[]
                 {
                     ($"UPDATE HoldRecords SET Reason = N'Other' WHERE Id = {hold.Id}", "only the release columns"),
                     ($"DELETE FROM HoldRecords WHERE Id = {hold.Id}", "cannot be deleted"),
                 })
            Assert.Contains(message, (await Assert.ThrowsAsync<SqlException>(() => SqlAsync(sql))).Message);

        await SqlAsync($"UPDATE HoldRecords SET ReleasedAt = SYSUTCDATETIME(), ReleasedBy = 'qc01', ReleaseNote = N'Use as is' WHERE Id = {hold.Id}");
        var again = await Assert.ThrowsAsync<SqlException>(() =>
            SqlAsync($"UPDATE HoldRecords SET ReleaseNote = N'Changed my mind' WHERE Id = {hold.Id}"));
        Assert.Contains("released hold cannot be changed", again.Message);
    }

    // Q17 in the database too, and FAI records are insert-only.
    [Fact]
    public async Task Fai_cannot_pass_with_a_red_automatic_check_and_is_insert_only()
    {
        var s = await SerialAsync();
        var template = await api.WithDbAsync(db => db.FaiTemplates.Where(t => t.IsActive).Select(t => t.Id).FirstAsync());

        var pass = await Assert.ThrowsAsync<SqlException>(() => SqlAsync($"""
            INSERT INTO FaiRecords (WorkOrderId, SerialId, FaiTemplateId, ChecklistJson, AutoChecksPassed, Result, InspectedBy, InspectedAt)
            VALUES ({s.WorkOrderId}, {s.SerialId}, {template}, N'[]', 0, 'Pass', 'qc01', SYSUTCDATETIME())
            """));
        Assert.Contains("CK_FaiRecords_PassNeedsAuto", pass.Message);
        // [step 8b] a Fail without the reason the sample went to Repair
        var noDefect = await Assert.ThrowsAsync<SqlException>(() => SqlAsync($"""
            INSERT INTO FaiRecords (WorkOrderId, SerialId, FaiTemplateId, ChecklistJson, AutoChecksPassed, Result, InspectedBy, InspectedAt)
            VALUES ({s.WorkOrderId}, {s.SerialId}, {template}, N'[]', 0, 'Fail', 'qc01', SYSUTCDATETIME())
            """));
        Assert.Contains("CK_FaiRecords_DefectOnFail", noDefect.Message);

        var fail = new FaiRecord(s.WorkOrderId, s.SerialId, template, "[]", autoChecksPassed: false, FaiResult.Fail, "Wrong polarity on D3", "qc01", T0, "FAI-FAIL");
        await AddAsync(fail);
        Assert.Contains("append-only", (await Assert.ThrowsAsync<SqlException>(() =>
            SqlAsync($"UPDATE FaiRecords SET Result = 'Pass', AutoChecksPassed = 1 WHERE Id = {fail.Id}"))).Message);
    }

    private sealed record SerialSetup(int WorkOrderId, long SerialId, int StationId);

    private Task<SerialSetup> SerialAsync() => api.WithDbAsync(async db =>
    {
        var station = await db.Stations.SingleAsync(s => s.Code == "L1-TEST-01");
        var pcbA = await db.Items.SingleAsync(i => i.Code == "PCB-A");
        var wo = WorkOrder.Create(ApiFixture.Unique("WO-QS"), pcbA.Id, station.LineId, 10, T0, T0.AddHours(8), requireFai: false);
        db.WorkOrders.Add(wo);
        await db.SaveChangesAsync();
        var serial = new Serial(SerialFormat.Build("QSCH", new DateOnly(2026, 11, 3), Interlocked.Increment(ref _nextSerial)), wo.Id, pcbA.Id);
        db.Serials.Add(serial);
        await db.SaveChangesAsync();
        return new SerialSetup(wo.Id, serial.Id, station.Id);
    });

    private Task<InspectionPlan> RailAsync() => api.WithDbAsync(db => db.InspectionPlans.SingleAsync(p => p.Code == QualitySeeder.RailPlan));

    // A plan of its own, so alerts and baselines of other tests do not count. Schema tests only: the use case of
    // step 5 decides which sources a plan accepts.
    private Task<InspectionPlan> PlanAsync() => api.WithDbAsync(async db =>
    {
        var plan = InspectionPlan.ForMachine(ApiFixture.Unique("QS"), "Schema test", "V", 3.135m, 3.465m, null);
        db.InspectionPlans.Add(plan);
        await db.SaveChangesAsync();
        return plan;
    });

    private Task<long> AddAsync(Measurement m) => api.WithDbAsync(async db => { db.Measurements.Add(m); await db.SaveChangesAsync(); return m.Id; });
    private Task<int> AddAsync(object entity) => api.WithDbAsync(db => { db.Add(entity); return db.SaveChangesAsync(); });
    private Task<int> SqlAsync(FormattableString sql) => api.WithDbAsync(db => db.Database.ExecuteSqlAsync(sql));

    // Other tests (M4) add "peakTemp" to machines of their own: the seeded one is on L1-REFLOW-01.
    private static Task<MiniMes.Domain.Machines.MachineTag> ReflowTagAsync(AppDbContext db)
        => (from t in db.MachineTags join m in db.Machines on t.MachineId equals m.Id
            where m.Code == "L1-REFLOW-01" && t.TagName == "peakTemp" select t).SingleAsync();

    private static async Task<long> NextNcrAsync(AppDbContext db)
        => (await db.Database.SqlQueryRaw<long>("SELECT NEXT VALUE FOR dbo.NcrSeq AS [Value]").ToListAsync()).Single();
}
