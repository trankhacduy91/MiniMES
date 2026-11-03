using Microsoft.EntityFrameworkCore;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Fai;

namespace MiniMes.Infrastructure.Persistence.Seed;

// Spec M7 step 2: the two characteristics of the demo and the FAI checklist of PCB-A. Runs after MasterDataSeeder
// (needs PCB-A, the defect codes and the reflow tag), in Development and Testing. Idempotent per record like it.
public class QualitySeeder(AppDbContext db, TimeProvider time)
{
    public const string RailPlan = "3V3_RAIL", ReflowPlan = "REFLOW_PEAK_TEMP";
    public const string SeedBy = "seed";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var pcbA = await db.Items.Where(i => i.Code == "PCB-A").Select(i => (int?)i.Id).SingleOrDefaultAsync(ct);
        if (pcbA is null) return;   // master data not seeded

        await SeedPlansAsync(pcbA.Value, ct);
        await SeedBaselinesAsync(ct);
        await LinkReflowTagAsync(ct);
        await SeedFaiTemplateAsync(pcbA.Value, ct);
    }

    private async Task SeedPlansAsync(int pcbA, CancellationToken ct)
    {
        var existing = await db.InspectionPlans.Select(p => p.Code).ToListAsync(ct);
        if (!existing.Contains(RailPlan))
        {
            var ngCode = await db.DefectCodes.Where(d => d.Code == MasterDataSeeder.OutOfSpecDefect).Select(d => d.Id).SingleAsync(ct);
            // Spec 1.2: the customer's limits for the 3.3 V rail, measured at TEST on every board.
            db.InspectionPlans.Add(InspectionPlan.ForStation(RailPlan, "3.3 V rail voltage", pcbA, "TEST", "V",
                lsl: 3.135m, usl: 3.465m, target: 3.300m, defectCodeId: ngCode, frequency: "Every board"));
        }
        if (!existing.Contains(ReflowPlan))
            // [v2] 2.3b: the oven peak temperature, sampled from the machine tag.
            db.InspectionPlans.Add(InspectionPlan.ForMachine(ReflowPlan, "Reflow peak temperature", "°C",
                lsl: 235m, usl: 255m, target: 245m, frequency: "Every 30 s from L1-REFLOW-01/peakTemp"));
        await db.SaveChangesAsync(ct);
    }

    // Phase I baselines given by the spec (Q3: fixed). Not computed from data here, so no MR̄ and no sample count;
    // QC replaces them with RecomputeControlLimits (step 5) once real points exist.
    private static readonly (string Plan, decimal Mean, decimal Sigma)[] Baselines =
    [
        (RailPlan, 3.302m, 0.040m),
        (ReflowPlan, 245.0m, 0.8m),
    ];

    private async Task SeedBaselinesAsync(CancellationToken ct)
    {
        var plans = await db.InspectionPlans.ToDictionaryAsync(p => p.Code, p => p.Id, ct);
        var withBaseline = await db.ControlLimitSets.Where(c => c.IsActive).Select(c => c.InspectionPlanId).ToListAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var b in Baselines.Where(b => plans.ContainsKey(b.Plan) && !withBaseline.Contains(plans[b.Plan])))
            db.ControlLimitSets.Add(new ControlLimitSet(plans[b.Plan], b.Mean, b.Sigma, mrBar: null, sampleCount: 0,
                baselineFrom: null, baselineTo: null, reason: "Initial baseline (spec M7 1.2)", calculatedBy: SeedBy, now));
        await db.SaveChangesAsync(ct);
    }

    private async Task LinkReflowTagAsync(CancellationToken ct)
    {
        var tag = await (from t in db.MachineTags
                         join m in db.Machines on t.MachineId equals m.Id
                         where m.Code == "L1-REFLOW-01" && t.TagName == "peakTemp"
                         select t).SingleOrDefaultAsync(ct);
        if (tag is null || tag.SpcCharacteristicId is not null) return;   // someone already linked it: leave it
        var plan = await db.InspectionPlans.Where(p => p.Code == ReflowPlan).Select(p => p.Id).SingleAsync(ct);
        tag.LinkSpc(plan, sampleEverySec: 30);
        await db.SaveChangesAsync(ct);
    }

    // [v2] 2.5b: the three automatic checks and what QC looks at on the first board.
    private static readonly FaiCheckItem[] PcbAFai =
    [
        new(nameof(FaiAutoCheck.BomMatch), "Components traced on the sample match the work order BOM", FaiCheckKind.Auto),
        new(nameof(FaiAutoCheck.TestPassed), "The sample passed every inspection step", FaiCheckKind.Auto),
        new(nameof(FaiAutoCheck.MeasurementsInSpec), "Every measurement of the sample is in specification", FaiCheckKind.Auto),
        new("VISUAL", "No bridge, missing or shifted component under the magnifier", FaiCheckKind.Manual),
        new("POLARITY", "Diodes and electrolytic capacitors face the right way", FaiCheckKind.Manual),
        new("LABEL", "Serial label readable and in its place", FaiCheckKind.Manual),
    ];

    private async Task SeedFaiTemplateAsync(int pcbA, CancellationToken ct)
    {
        if (await db.FaiTemplates.AnyAsync(t => t.ItemId == pcbA, ct)) return;
        db.FaiTemplates.Add(new FaiTemplate(pcbA, version: 1, PcbAFai));
        await db.SaveChangesAsync(ct);
    }
}
