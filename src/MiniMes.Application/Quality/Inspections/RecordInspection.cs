using System.Diagnostics;
using System.Globalization;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Application.Execution;
using MiniMes.Application.Quality.Spc;
using MiniMes.Application.Realtime;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Quality;
using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.Planning;
using MiniMes.Domain.Quality;

namespace MiniMes.Application.Quality.Inspections;

// Spec M7 2.3: one request -> route check (M3) -> judge against the spec -> store the measurements -> SPC rules ->
// alerts, and the board goes OK or NG through the scan of M3. ONE transaction: a value is never stored without its
// scan, and a scan is never recorded without its values.
public sealed record RecordInspectionCommand(int StationId, string SerialNo, IReadOnlyList<InspectionValue> Values,
    string Source, Guid? ClientRequestId) : IRequest<InspectionResponse>;

public sealed class RecordInspectionValidator : AbstractValidator<RecordInspectionCommand>
{
    public const int MaxValues = 20;

    public RecordInspectionValidator()
    {
        RuleFor(x => x.SerialNo).NotEmpty().MaximumLength(Serial.SerialNoMaxLength);
        RuleFor(x => x.Values).NotEmpty().Must(v => v.Count <= MaxValues).WithMessage($"At most {MaxValues} values per request.")
            .Must(v => v.Select(i => i.PlanCode?.Trim().ToUpperInvariant()).Distinct().Count() == v.Count)
            .WithMessage("A characteristic is listed twice.");
        RuleForEach(x => x.Values).ChildRules(v =>
        {
            v.RuleFor(i => i.PlanCode).NotEmpty().MaximumLength(MasterDataRules.CodeMaxLength);
            // Stored as decimal(18,6): a 7th decimal would be rounded on save, after it was judged (3.4650004 judged
            // NG, stored 3.465000 which reads OK).
            v.RuleFor(i => i.Value).Must(d => decimal.Round(d, 6) == d).WithMessage("At most 6 decimals.")
                .InclusiveBetween(-999_999_999_999m, 999_999_999_999m);
        });
        RuleFor(x => x.Source).Must(s => Enum.TryParse<ScanSource>(s, ignoreCase: true, out _))
            .WithMessage("Source must be Kiosk or Machine.");
        RuleFor(x => x.ClientRequestId).NotEqual(Guid.Empty);
    }
}

public sealed class RecordInspectionHandler(IAppDbContext db, ScanExecutor scans, ISpcEvaluator spc, ICurrentUser user,
    TimeProvider time, IRealtimeNotifier notifier, ILogger<RecordInspectionHandler> log) : IRequestHandler<RecordInspectionCommand, InspectionResponse>
{
    public async Task<InspectionResponse> Handle(RecordInspectionCommand cmd, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var serialNo = ScanExecutor.Normalize(cmd.SerialNo);

        // A retry of a request already stored: the first answer again, nothing written (2.3 trap 3).
        if (cmd.ClientRequestId is { } requestId && await ReplayAsync(requestId, ct) is { } replay) return replay;

        var station = await db.LoadStationAsync(cmd.StationId, ct);   // 404
        var plans = await LoadPlansAsync(cmd.Values, ct);
        await CheckPlansMeasuredHereAsync(plans.Values, serialNo, station, ct);

        // The server decides (2.3): any value out of spec makes the board NG, with the defect code of the first one.
        var judged = cmd.Values.Select(v => (Plan: plans[Code(v.PlanCode)], v.Value)).ToList();
        var outOfSpec = judged.Where(j => j.Plan.Plan.Judge(j.Value) == MeasureResult.Ng).ToList();
        var scanCmd = new ScanCommand(station.Id, serialNo, outOfSpec.Count == 0 ? "OK" : "NG",
            outOfSpec.Count == 0 ? null : outOfSpec[0].Plan.DefectCode, cmd.Source);

        InspectionResponse response;
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            // Route check first: a board not at this step, on Hold or unknown is answered Blocked, nothing written.
            var scan = await scans.ExecuteAsync(tx, scanCmd, ct);
            if (!scan.Recorded)
                response = Answer(scan.Response, [], []);
            else
            {
                var serialId = await db.Serials.Where(s => s.SerialNo == serialNo).Select(s => s.Id).SingleAsync(ct);
                var source = Enum.Parse<ScanSource>(cmd.Source, ignoreCase: true) == ScanSource.Machine
                    ? MeasurementSource.Station : MeasurementSource.Manual;
                var now = time.GetUtcNow().UtcDateTime;
                var measurements = judged.Select(j => Measurement.OfSerial(j.Plan.Plan, source, serialId, station.Id, j.Value,
                    user.EmployeeCode!, now, cmd.ClientRequestId)).ToList();
                db.Measurements.AddRange(measurements);
                await db.SaveChangesAsync(ct);

                var alerts = new List<SpcAlert>();
                foreach (var m in measurements) alerts.AddRange(await spc.EvaluateAsync(tx, m, ct));
                await tx.CommitAsync(ct);

                var codes = plans.Values.ToDictionary(p => p.Plan.Id, p => p.Plan.Code);
                response = Answer(scan.Response with { Message = Message(scan.Response, outOfSpec) },
                    measurements.Select(m => Dto(m, codes[m.InspectionPlanId])).ToList(),
                    alerts.Select(a => QualityDtos.Alert(a, codes[a.InspectionPlanId])).ToList());
            }
        }
        // After the commit: an open chart refreshes at once (step 7). A failed push is the notifier's problem, not
        // the measurement's.
        foreach (var alert in response.Alerts) await notifier.SpcAlertRaisedAsync(alert, ct);

        log.LogInformation("Inspection {SerialNo} at station {StationId}: {InspectionResult} {ReasonCode} values={ValueCount} alerts={AlertCount} duplicate={Duplicate} in {ElapsedMs} ms",
            serialNo, cmd.StationId, response.Result, response.ReasonCode, cmd.Values.Count, response.Alerts.Count, response.Duplicate,
            watch.ElapsedMilliseconds);
        return response;
    }

    private sealed record PlanRow(InspectionPlan Plan, string? DefectCode);

    private static string Code(string planCode) => planCode.Trim().ToUpperInvariant();

    private async Task<Dictionary<string, PlanRow>> LoadPlansAsync(IReadOnlyList<InspectionValue> values, CancellationToken ct)
    {
        var codes = values.Select(v => Code(v.PlanCode)).ToList();
        var plans = await (from p in db.InspectionPlans.AsNoTracking()
                           where codes.Contains(p.Code) && p.IsActive
                           join d in db.DefectCodes on p.DefectCodeId equals d.Id into dj
                           from d in dj.DefaultIfEmpty()
                           select new PlanRow(p, d.Code)).ToDictionaryAsync(r => r.Plan.Code, ct);
        var failures = codes.Where(c => !plans.ContainsKey(c))
            .Select(c => new ValidationFailure("Values", $"Unknown or inactive inspection plan '{c}'."))
            .Concat(plans.Values.Where(p => p.Plan.ItemId is null)
                .Select(p => new ValidationFailure("Values", $"Plan {p.Plan.Code} is sampled from a machine, not measured on a board.")))
            .ToList();
        if (failures.Count > 0) throw new ValidationException(failures);
        return plans;
    }

    // The plan belongs to the board's item and to an operation done at this station's work center. An unknown
    // serial is left to the scan, which answers SerialNotFound like any scan.
    private async Task CheckPlansMeasuredHereAsync(IEnumerable<PlanRow> plans, string serialNo, ScanContextReader.StationRow station,
        CancellationToken ct)
    {
        var board = await db.Serials.AsNoTracking().Where(s => s.SerialNo == serialNo)
            .Select(s => new { s.ItemId, s.WorkOrderId }).SingleOrDefaultAsync(ct);
        if (board is null) return;
        var operationsHere = await db.WorkOrderOperations.AsNoTracking()
            .Where(o => o.WorkOrderId == board.WorkOrderId && o.WorkCenterId == station.WorkCenterId)
            .Select(o => o.OperationCode).ToListAsync(ct);
        foreach (var p in plans.Select(r => r.Plan))
        {
            if (p.ItemId != board.ItemId) throw new DomainException($"Plan {p.Code} is not a characteristic of this board's item.");
            if (!operationsHere.Contains(p.OperationCode!))
                throw new DomainException($"Plan {p.Code} is measured at {p.OperationCode}, not at station {station.Code}.");
        }
    }

    private async Task<InspectionResponse?> ReplayAsync(Guid requestId, CancellationToken ct)
    {
        var stored = await (from m in db.Measurements.AsNoTracking()
                            join p in db.InspectionPlans on m.InspectionPlanId equals p.Id
                            where m.ClientRequestId == requestId
                            orderby m.Id
                            select new { m, p.Code }).ToListAsync(ct);
        if (stored.Count == 0) return null;
        var ng = stored.Any(s => s.m.Result == MeasureResult.Ng);
        return new InspectionResponse(ng ? "Ng" : "Ok", "Duplicate request ignored.", null, true,
            stored.Select(s => Dto(s.m, s.Code)).ToList(), [], null, null);
    }

    private static string Message(ScanResponse scan, List<(PlanRow Plan, decimal Value)> outOfSpec)
        => outOfSpec.Count == 0 ? scan.Message
            : "Out of specification: " + string.Join(", ", outOfSpec.Select(o =>
                $"{o.Plan.Plan.Code} {Num(o.Value)} {o.Plan.Plan.Unit} (limits {Num(o.Plan.Plan.Lsl)} to {Num(o.Plan.Plan.Usl)})"))
              + ". " + scan.Message;

    // decimal(18,6) reads back as 3.135000: show 3.135, the way the spec was written. Invariant: a log/API text.
    private static string Num(decimal? d) => d?.ToString("0.######", CultureInfo.InvariantCulture) ?? "-";

    private static InspectionResponse Answer(ScanResponse scan, IReadOnlyList<MeasurementResultDto> measurements, IReadOnlyList<SpcAlertDto> alerts)
        => new(scan.Result, scan.Message, scan.ReasonCode, scan.Duplicate, measurements, alerts, scan.NextStep, scan.NextStepName);

    private static MeasurementResultDto Dto(Measurement m, string planCode)
        => new(m.Id, planCode, m.Value, m.Result.ToString(), m.Lsl, m.Usl);
}
