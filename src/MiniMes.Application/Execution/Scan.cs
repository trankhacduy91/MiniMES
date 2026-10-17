using System.Diagnostics;
using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Contracts.Execution;
using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Execution;

// The scan (spec M3 2.2): read -> route check -> if allowed, write in ONE transaction the serial's new state,
// the ScanLog row and the step counter. Blocked or duplicate scans write nothing.
public sealed record ScanCommand(int StationId, string SerialNo, string Result, string? DefectCode, string Source)
    : IRequest<ScanResponse>;

public sealed class ScanValidator : AbstractValidator<ScanCommand>
{
    public ScanValidator()
    {
        RuleFor(x => x.SerialNo).NotEmpty().MaximumLength(Serial.SerialNoMaxLength);
        RuleFor(x => x.Result).Must(r => Enum.TryParse<ScanResult>(r, ignoreCase: true, out _))
            .WithMessage("Result must be OK or NG.");
        RuleFor(x => x.DefectCode).NotEmpty().MaximumLength(MasterDataRules.CodeMaxLength)
            .When(x => string.Equals(x.Result, "NG", StringComparison.OrdinalIgnoreCase))
            .WithMessage("An NG result needs a defect code.");
        RuleFor(x => x.DefectCode).Empty().When(x => string.Equals(x.Result, "OK", StringComparison.OrdinalIgnoreCase))
            .WithMessage("An OK result has no defect code.");
        RuleFor(x => x.Source).Must(s => Enum.TryParse<ScanSource>(s, ignoreCase: true, out _))
            .WithMessage("Source must be Kiosk or Machine.");
    }
}

public sealed class ScanHandler(IAppDbContext db, ICurrentUser user, TimeProvider time, ILogger<ScanHandler> log)
    : IRequestHandler<ScanCommand, ScanResponse>
{
    public async Task<ScanResponse> Handle(ScanCommand cmd, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        // Scanners in the wrong keyboard layout or a hand-typed serial: compare in one canonical form.
        var serialNo = cmd.SerialNo.Trim().ToUpperInvariant();
        var response = await ScanAsync(cmd, serialNo, ct);
        log.LogInformation("Scan {SerialNo} at station {StationId}: {ScanResult} {ReasonCode} duplicate={Duplicate} in {ElapsedMs} ms",
            serialNo, cmd.StationId, response.Result, response.ReasonCode, response.Duplicate, watch.ElapsedMilliseconds);
        return response;
    }

    private async Task<ScanResponse> ScanAsync(ScanCommand cmd, string serialNo, CancellationToken ct)
    {
        var result = Enum.Parse<ScanResult>(cmd.Result, ignoreCase: true);
        var source = Enum.Parse<ScanSource>(cmd.Source, ignoreCase: true);
        var station = await db.LoadStationAsync(cmd.StationId, ct);   // 404
        var isRepair = station.Type == Domain.Factory.StationType.Repair;
        if (isRepair && result == ScanResult.Ng)
            throw new ValidationException([new ValidationFailure("Result", "A scan at the Repair station is a check-in: send OK.")]);
        var defectCode = cmd.DefectCode?.Trim().ToUpperInvariant();
        if (defectCode is not null && !await db.DefectCodes.AnyAsync(d => d.Code == defectCode && d.IsActive, ct))
            throw new ValidationException([new ValidationFailure("DefectCode", $"Unknown or inactive defect code '{defectCode}'.")]);
        var operatorCode = user.EmployeeCode ?? throw new InvalidOperationException("A scan needs a logged-in user.");

        if (await db.OpenSessionWoIdAsync(station.Id, ct) is not { } sessionWoId)
            return Blocked(BlockReason.NoOpenSession, $"No operator session is open at station {station.Code}.");
        // A smudged label or a typo is answered at once, without touching the serial table (M2 check digit).
        if (!SerialFormat.HasValidCheckDigit(serialNo))
            return Blocked(BlockReason.SerialNotFound, $"'{serialNo}' is not a valid serial number (check digit).");

        // Two scans of the same board at once (double trigger, kiosk + machine): the loser's UPDATE finds a new
        // RowVersion (409 inside), forgets what it read and evaluates again; the second time it sees the first
        // scan and answers "duplicate" or a proper block (spec 2.3).
        for (var attempt = 0; ; attempt++)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var ctx = await db.LoadContextAsync(station, sessionWoId, serialNo, result, now, ct);
            var decision = RouteCheck.Evaluate(ctx);
            if (decision.Verdict == RouteVerdict.Block) return Blocked(decision.Reason, decision.Message);
            if (decision.Verdict == RouteVerdict.Duplicate) return Duplicate(ctx);
            try
            {
                return await RecordAsync(ctx, decision, isRepair, result, defectCode, operatorCode, source, ct);
            }
            catch (ConflictException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<ScanResponse> RecordAsync(ScanContext ctx, RouteDecision decision, bool isRepair, ScanResult result,
        string? defectCode, string operatorCode, ScanSource source, CancellationToken ct)
    {
        await using var tx = await db.BeginTransactionAsync(ct);
        var serial = await db.Serials.SingleAsync(s => s.SerialNo == ctx.Serial!.SerialNo, ct);   // tracked, RowVersion
        var steps = ctx.Operations.OrderBy(o => o.Seq).ToList();
        var step = decision.Step;
        var isLast = step is not null && step.Seq == steps[^1].Seq;

        if (isRepair) serial.CheckInRepair();
        else serial.ApplyScan(step!.Seq, isLast, result);

        // [v2] Rule 6: the sample is taken with a conditional UPDATE, not read-then-write: two SMT stations
        // scanning two boards at once cannot both become the first article (0 rows = someone else won).
        if (decision.AssignFaiSample)
        {
            var taken = await db.WorkOrders
                .Where(w => w.Id == serial.WorkOrderId && w.FaiStatus == FaiStatus.Pending && w.FaiSerialNo == null)
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.FaiSerialNo, serial.SerialNo), ct);
            if (taken == 0) return Blocked(BlockReason.FaiNotApproved, "First article not approved yet (waiting for QC).");   // rolls back
        }

        // The WO's first accepted production scan starts it (M2 Start); tracked so the audit log records it.
        WorkOrder? wo = null;
        if (!isRepair && ctx.WoStatus == WorkOrderStatus.Released)
        {
            wo = await db.WorkOrders.SingleAsync(w => w.Id == serial.WorkOrderId, ct);
            wo.Start(ctx.NowUtc);
        }

        db.ScanLogs.Add(new ScanLog(serial, ctx.Station.Id, step?.Seq ?? 0, result, defectCode, operatorCode, source, ctx.NowUtc));
        await db.SaveChangesAsync(ct);   // stale serial RowVersion -> ConflictException -> the caller retries

        if (!isRepair)
        {
            // Atomic counter in SQL ("SET QtyGood = QtyGood + 1"), never read-increment-write: two scans of the
            // same step would both read 100 and both write 101.
            var counter = db.WorkOrderOperations.Where(o => o.WorkOrderId == serial.WorkOrderId && o.Seq == step!.Seq);
            if (result == ScanResult.Ok) await counter.ExecuteUpdateAsync(s => s.SetProperty(o => o.QtyGood, o => o.QtyGood + 1), ct);
            else await counter.ExecuteUpdateAsync(s => s.SetProperty(o => o.QtyNg, o => o.QtyNg + 1), ct);

            // Rule R10 of M2: the planned good quantity at the last step completes the order.
            if (result == ScanResult.Ok && isLast)
            {
                var good = await counter.Select(o => o.QtyGood).SingleAsync(ct);
                wo ??= await db.WorkOrders.SingleAsync(w => w.Id == serial.WorkOrderId, ct);
                if (wo.TryAutoComplete(good, ctx.NowUtc)) await db.SaveChangesAsync(ct);
            }
        }
        await tx.CommitAsync(ct);

        if (isRepair) return new ScanResponse("Ok", "Repair check-in: repair the board, then complete the repair.", null, null, null, false);
        if (result == ScanResult.Ng) return new ScanResponse("Ng", $"NG recorded ({defectCode}): send the board to Repair.", null, null, null, false);
        var next = steps.FirstOrDefault(o => o.Seq > step!.Seq);
        return new ScanResponse("Ok", next is null ? "OK - board completed." : $"OK - next: {next.OperationCode}.",
            null, next?.Seq, next?.OperationCode, false);
    }

    // Same answer as the first scan, nothing written (spec 2.3): HTTP 200, so a machine does not retry forever.
    private static ScanResponse Duplicate(ScanContext ctx)
    {
        var serial = ctx.Serial!;
        var next = serial.Status is SerialStatus.Created or SerialStatus.InProcess
            ? ctx.Operations.OrderBy(o => o.Seq).FirstOrDefault(o => o.Seq > serial.CurrentSeq)
            : null;
        return new ScanResponse(ctx.LastScanHere!.Result == ScanResult.Ok ? "Ok" : "Ng", "Duplicate scan ignored.", null,
            next?.Seq, next?.OperationCode, true);
    }

    private static ScanResponse Blocked(BlockReason reason, string message)
        => new("Blocked", message, reason.ToString(), null, null, false);
}
