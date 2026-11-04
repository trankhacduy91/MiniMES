using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MiniMes.Application.Common;
using MiniMes.Application.Material;
using MiniMes.Contracts.Execution;
using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Execution;

// Recorded = this call wrote the scan (ScanLog, serial, counters). False for blocked and duplicate scans, which
// wrote nothing: the caller must not commit anything on their behalf.
public sealed record ScanOutcome(ScanResponse Response, bool Recorded);

// The core of the scan (spec M3 2.2), shared by ScanHandler (M3) and PackHandler (M3b 2.2): read -> route check ->
// if allowed, write the serial's new state, the ScanLog row and the step counter. It never opens or commits a
// transaction: the outermost handler owns it, so a pack is "scan + carton" in ONE transaction (P1).
public sealed class ScanExecutor(IAppDbContext db, ICurrentUser user, TimeProvider time, IMaterialGate materialGate,
    IPublisher publisher)
{
    private const string Savepoint = "scan";

    // Scanners in the wrong keyboard layout or a hand-typed serial: compare in one canonical form.
    public static string Normalize(string serialNo) => serialNo.Trim().ToUpperInvariant();

    public async Task<ScanOutcome> ExecuteAsync(IDbContextTransaction tx, ScanCommand cmd, CancellationToken ct)
    {
        var serialNo = Normalize(cmd.SerialNo);
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
        // scan and answers "duplicate" or a proper block (spec 2.3). The savepoint undoes the first try's writes
        // (FAI sample, WO start) without losing what the caller did before in the same transaction.
        for (var attempt = 0; ; attempt++)
        {
            var now = time.GetUtcNow().UtcDateTime;
            var ctx = await db.LoadContextAsync(station, sessionWoId, serialNo, result, now, ct);
            var decision = RouteCheck.Evaluate(ctx);
            if (decision.Verdict == RouteVerdict.Block) return Blocked(decision.Reason, decision.Message);
            if (decision.Verdict == RouteVerdict.Duplicate) return new ScanOutcome(Duplicate(ctx), false);
            // M6 R8: the parts this step puts on the board must come from a known, usable lot. Before any write:
            // a blocked scan writes nothing, like any other block.
            if (!isRepair && result == ScanResult.Ok
                && await materialGate.CheckAsync(station.Id, ctx.Serial!.WorkOrderId, decision.Step!.Seq, now, ct) is { } missing)
                return Blocked(BlockReason.MaterialNotLoaded, missing);

            await tx.CreateSavepointAsync(Savepoint, ct);
            try
            {
                var outcome = await RecordAsync(ctx, decision, isRepair, result, defectCode, operatorCode, source, ct);
                if (!outcome.Recorded) await UndoAsync(tx, ct);
                return outcome;
            }
            catch (ConflictException) when (attempt == 0)
            {
                await UndoAsync(tx, ct);
            }
        }
    }

    private async Task UndoAsync(IDbContextTransaction tx, CancellationToken ct)
    {
        await tx.RollbackToSavepointAsync(Savepoint, ct);
        db.ChangeTracker.Clear();
    }

    private async Task<ScanOutcome> RecordAsync(ScanContext ctx, RouteDecision decision, bool isRepair, ScanResult result,
        string? defectCode, string operatorCode, ScanSource source, CancellationToken ct)
    {
        var serial = await db.Serials.SingleAsync(s => s.SerialNo == ctx.Serial!.SerialNo, ct);   // tracked, RowVersion
        // [M7 step 9] The route check judged the board as the context read it; something committed in between (a Hold,
        // another scan) means that judgement is stale: re-evaluate, like a RowVersion conflict, instead of a 422 here.
        if (serial.Status != ctx.Serial!.Status || serial.CurrentSeq != ctx.Serial.CurrentSeq)
            throw new ConflictException($"Serial {serial.SerialNo} changed while it was being scanned.");
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
            if (taken == 0) return Blocked(BlockReason.FaiNotApproved, "First article not approved yet (waiting for QC).");   // undone
        }

        // The WO's first accepted production scan starts it (M2 Start); tracked so the audit log records it.
        // EnsureStarted, not Start: another board may have started it since the context was read. Two starts at
        // the same instant both write the WO: its RowVersion makes one fail and retry (spec 2.3).
        WorkOrder? wo = null;
        if (!isRepair && ctx.WoStatus == WorkOrderStatus.Released)
        {
            wo = await db.WorkOrders.SingleAsync(w => w.Id == serial.WorkOrderId, ct);
            wo.EnsureStarted(ctx.NowUtc);
        }

        var scanLog = new ScanLog(serial, ctx.Station.Id, step?.Seq ?? 0, result, defectCode, operatorCode, source, ctx.NowUtc);
        db.ScanLogs.Add(scanLog);
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

            // M6: material consumption (SerialComponent, lot quantity) in this same transaction.
            if (result == ScanResult.Ok)
                await publisher.Publish(new SerialScannedOk(serial.Id, serial.SerialNo, serial.WorkOrderId, ctx.Station.Id,
                    step!.Seq, scanLog.Id, ctx.NowUtc), ct);
        }

        if (isRepair) return Recorded(new("Ok", "Repair check-in: repair the board, then complete the repair.", null, null, null, false));
        if (result == ScanResult.Ng) return Recorded(new("Ng", $"NG recorded ({defectCode}): send the board to Repair.", null, null, null, false));
        var next = steps.FirstOrDefault(o => o.Seq > step!.Seq);
        return Recorded(new("Ok", next is null ? "OK - board completed." : $"OK - next: {next.OperationCode}.",
            null, next?.Seq, next?.OperationCode, false));
    }

    private static ScanOutcome Recorded(ScanResponse response) => new(response, true);

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

    private static ScanOutcome Blocked(BlockReason reason, string message)
        => new(new ScanResponse("Blocked", message, reason.ToString(), null, null, false), false);
}
