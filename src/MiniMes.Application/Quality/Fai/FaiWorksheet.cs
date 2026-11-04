using Microsoft.EntityFrameworkCore;
using MiniMes.Application.Common;
using MiniMes.Application.Material;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;
using MiniMes.Domain.Quality.Fai;

namespace MiniMes.Application.Quality.Fai;

// What the worksheet knows about one WO's first article: the sample, whether it can be judged now (Q17), the
// active checklist of the item and the sheet filled from MES data.
internal sealed record FaiWorksheetData(
    string ItemCode,
    string Line,
    Serial? Sample,
    string? NotReadyReason,
    FaiTemplate? Template,
    FaiSheet Sheet)
{
    public bool SampleReady => NotReadyReason is null;
}

// Spec M7 2.5b, the three automatic checks. Reads only: the same code fills the worksheet QC looks at and checks
// again when QC records the result (the server never trusts a green it computed minutes ago).
internal static class FaiWorksheet
{
    public const string DefaultDefectCode = "FAI-FAIL";

    // wo must have its Operations loaded. ticked: the manual items QC checked (null = blank worksheet).
    public static async Task<FaiWorksheetData> ReadAsync(this IAppDbContext db, WorkOrder wo, IReadOnlyCollection<string>? ticked,
        CancellationToken ct)
    {
        var itemCode = await db.Items.Where(i => i.Id == wo.ItemId).Select(i => i.Code).SingleAsync(ct);
        var line = await db.Lines.Where(l => l.Id == wo.LineId).Select(l => l.Code).SingleAsync(ct);
        var template = await db.FaiTemplates.AsNoTracking().SingleOrDefaultAsync(t => t.ItemId == wo.ItemId && t.IsActive, ct);
        var sample = wo.FaiSerialNo is { } sn ? await db.Serials.SingleOrDefaultAsync(s => s.SerialNo == sn, ct) : null;

        var steps = wo.Operations.OrderBy(o => o.Seq).ToList();
        var inspections = steps.Where(o => o.IsInspection).ToList();
        // Q17: judged once the sample passed the last inspection step (Test); the routing's last step if it has none.
        var gate = inspections.Count > 0 ? inspections[^1] : steps[^1];

        var auto = sample is null
            ? Enum.GetValues<FaiAutoCheck>().ToDictionary(c => c, _ => new FaiAutoResult(false, "No sample yet."))
            : await AutoChecksAsync(db, wo, sample, steps, inspections, ct);
        var sheet = FaiChecks.Fill(template?.Items() ?? [], auto, ticked);

        string? notReady = (wo.FaiStatus, sample, template) switch
        {
            (not FaiStatus.Pending, _, _) => $"First article is {wo.FaiStatus?.ToString() ?? "not required before release"}.",
            (_, null, _) => "No sample yet: the first board scanned at the first step becomes the sample.",
            (_, _, null) => $"Item {itemCode} has no active FAI checklist.",
            _ when sample.Status is not (SerialStatus.InProcess or SerialStatus.Completed)
                => $"Sample {sample.SerialNo} is {sample.Status}: it must pass {gate.OperationCode} again first.",
            _ when sample.CurrentSeq < gate.Seq => $"Sample {sample.SerialNo} has not passed {gate.OperationCode} yet.",
            _ => null,
        };
        return new FaiWorksheetData(itemCode, line, sample, notReady, template, sheet);
    }

    private static async Task<Dictionary<FaiAutoCheck, FaiAutoResult>> AutoChecksAsync(IAppDbContext db, WorkOrder wo, Serial sample,
        List<WorkOrderOperation> steps, List<WorkOrderOperation> inspections, CancellationToken ct)
    {
        var passed = steps.Where(o => o.Seq <= sample.CurrentSeq).ToList();

        // 1. BomMatch: lot-controlled snapshot lines of the passed steps vs the sample's backward trace (M6).
        var requiredIds = (await db.RequirementsAsync(wo.Id, ct)).Where(r => r.OperationSeq <= sample.CurrentSeq)
            .Select(r => r.ItemId).Distinct().ToList();
        var tracedIds = await db.SerialComponents.Where(c => c.SerialId == sample.Id).Select(c => c.ItemId).Distinct().ToListAsync(ct);
        var ids = requiredIds.Concat(tracedIds).Distinct().ToList();
        var codes = await db.Items.Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Code, ct);
        var bom = FaiChecks.BomMatch(requiredIds.Select(i => codes[i]).ToList(), tracedIds.Select(i => codes[i]).ToList());

        // 2. TestPassed: an OK scan of the sample at each inspection step (M3 ScanLog).
        var inspectionSeqs = inspections.Select(o => o.Seq).ToList();
        var okSeqs = await db.ScanLogs.Where(l => l.SerialId == sample.Id && l.WorkOrderId == wo.Id
                                                  && l.Result == Domain.Execution.ScanResult.Ok && inspectionSeqs.Contains(l.Seq))
            .Select(l => l.Seq).Distinct().ToListAsync(ct);
        var test = FaiChecks.TestPassed(inspections.Select(o => o.OperationCode).ToList(),
            inspections.Where(o => okSeqs.Contains(o.Seq)).Select(o => o.OperationCode).ToList());

        // 3. MeasurementsInSpec: the sample's values (a corrected one by its correction, Q16) and the station plans of
        // the item at the steps it passed.
        var opCodes = passed.Select(o => o.OperationCode).ToList();
        var planned = await db.InspectionPlans.Where(p => p.IsActive && p.ItemId == wo.ItemId && p.OperationCode != null
                                                          && opCodes.Contains(p.OperationCode))
            .Select(p => p.Code).ToListAsync(ct);
        var values = await (from m in db.Measurements
                            join p in db.InspectionPlans on m.InspectionPlanId equals p.Id
                            where m.SerialId == sample.Id
                                  && !db.Measurements.Any(c => c.CorrectsMeasurementId == m.Id)
                            orderby m.MeasuredAt
                            select new { p.Code, m.Value, m.Result }).ToListAsync(ct);
        var measured = FaiChecks.MeasurementsInSpec(planned,
            values.Select(v => (v.Code, v.Value, v.Result == MeasureResult.Ok)).ToList());

        return new()
        {
            [FaiAutoCheck.BomMatch] = bom,
            [FaiAutoCheck.TestPassed] = test,
            [FaiAutoCheck.MeasurementsInSpec] = measured,
        };
    }

    public static FaiCheckDto ToDto(this FaiCheckOutcome o) => new(o.Code, o.Text, o.Kind.ToString(), o.Passed, o.Detail);

    public static async Task<FaiWorksheetDto> ToDtoAsync(this IAppDbContext db, WorkOrder wo, FaiWorksheetData data, CancellationToken ct)
    {
        var history = await (from r in db.FaiRecords.AsNoTracking()
                             join s in db.Serials on r.SerialId equals s.Id
                             join t in db.FaiTemplates on r.FaiTemplateId equals t.Id
                             where r.WorkOrderId == wo.Id
                             orderby r.InspectedAt descending, r.Id descending
                             select new { r, s.SerialNo, t.Version }).ToListAsync(ct);
        return new FaiWorksheetDto(wo.Id, wo.WoNo, data.ItemCode, data.Line, wo.FaiStatus?.ToString(), data.Sample?.SerialNo,
            data.Sample?.Status.ToString(), data.SampleReady, data.NotReadyReason, data.Template?.Version,
            data.Sheet.Items.Select(i => i.ToDto()).ToList(), data.Sheet.AutoChecksPassed,
            history.Select(h => new FaiRecordDto(h.r.Id, h.SerialNo, h.Version, h.r.Result.ToString(), h.r.AutoChecksPassed,
                h.r.DefectCode, h.r.Note, h.r.InspectedBy, h.r.InspectedAt,
                FaiSheet.FromJson(h.r.ChecklistJson).Items.Select(i => i.ToDto()).ToList())).ToList(),
            Convert.ToBase64String(wo.RowVersion));
    }
}
