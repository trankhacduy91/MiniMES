using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MiniMes.Application.Common;
using MiniMes.Application.Material;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Factory;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.Material;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Execution;

// ---------- Complete a repair (spec M3 step 4) ----------

// The board was checked in by a scan at the Repair station (Ng -> Repair). The technician records what was
// done; the board goes back to the failed step, or to an earlier one (rework, ReturnToSeq).
// M6 step 10b: the lot of a replaced part becomes a SerialComponent of the board, like a scan OK at a station.
public sealed record CompleteRepairCommand(int StationId, string SerialNo, string DefectCode, string Action,
    string? ComponentReplaced, int? ReturnToSeq, string? ReplacementLotNo = null) : IRequest<RepairResponse>;

public sealed class CompleteRepairValidator : AbstractValidator<CompleteRepairCommand>
{
    public CompleteRepairValidator()
    {
        RuleFor(x => x.SerialNo).NotEmpty().MaximumLength(Serial.SerialNoMaxLength);
        RuleFor(x => x.DefectCode).NotEmpty().MaximumLength(MasterDataRules.CodeMaxLength);
        RuleFor(x => x.Action).NotEmpty().MaximumLength(RepairLog.ActionMaxLength);
        RuleFor(x => x.ComponentReplaced).MaximumLength(RepairLog.ComponentMaxLength);
        // Spec 1.5: a replaced part without its reference breaks traceability of the new component.
        RuleFor(x => x.ComponentReplaced).NotEmpty()
            .When(x => IsReplacement(x.Action))
            .WithMessage("A replacement needs the component replaced (reference and lot).");
        RuleFor(x => x.ReturnToSeq).GreaterThan(0).When(x => x.ReturnToSeq is not null);
        RuleFor(x => x.ReplacementLotNo).MaximumLength(MaterialLot.LotNoMaxLength);
    }

    public static bool IsReplacement(string? action) => action?.TrimStart().StartsWith("Replace", StringComparison.OrdinalIgnoreCase) == true;
}

public sealed class CompleteRepairHandler(IAppDbContext db, ICurrentUser user, TimeProvider time, IOptions<MaterialOptions> material)
    : IRequestHandler<CompleteRepairCommand, RepairResponse>
{
    // One replaced part per repair record (the technician records each part as its own repair if needed).
    private const decimal PartsPerReplacement = 1m;

    public async Task<RepairResponse> Handle(CompleteRepairCommand cmd, CancellationToken ct)
    {
        var station = await db.LoadStationAsync(cmd.StationId, ct);   // 404
        if (station.Type != StationType.Repair)
            throw new DomainException($"Station {station.Code} is not a Repair station.");
        var defectCode = cmd.DefectCode.Trim().ToUpperInvariant();
        if (!await db.DefectCodes.AnyAsync(d => d.Code == defectCode && d.IsActive, ct))
            throw new ValidationException([new ValidationFailure("DefectCode", $"Unknown or inactive defect code '{defectCode}'.")]);
        var technician = user.EmployeeCode ?? throw new InvalidOperationException("A repair needs a logged-in user.");

        var serialNo = cmd.SerialNo.Trim().ToUpperInvariant();
        var serial = await db.Serials.SingleOrDefaultAsync(s => s.SerialNo == serialNo, ct)
                     ?? throw new KeyNotFoundException($"Serial {serialNo} not found.");
        var steps = await db.WorkOrderOperations.AsNoTracking().Where(o => o.WorkOrderId == serial.WorkOrderId)
            .OrderBy(o => o.Seq).Select(o => new { o.Seq, o.OperationCode }).ToListAsync(ct);

        serial.CompleteRepair(cmd.ReturnToSeq, steps.Select(o => o.Seq).ToList());   // not in Repair / bad step -> 422
        var now = time.GetUtcNow().UtcDateTime;
        var lot = await ReplacementLotAsync(cmd, serial.WorkOrderId, now, ct);

        await using var tx = await db.BeginTransactionAsync(ct);
        var repair = new RepairLog(serial, station.Id, defectCode, cmd.Action, cmd.ComponentReplaced, cmd.ReturnToSeq, technician, now);
        db.RepairLogs.Add(repair);
        await db.SaveChangesAsync(ct);   // a second "complete" of the same board -> 409 or 422; gives repair.Id
        if (lot is not null)
        {
            // R9 keeps one row per (serial, station, item, lot): the same reel used twice on one board is already on
            // record, so only the quantity goes down again.
            var recorded = await db.SerialComponents.AnyAsync(c => c.SerialId == serial.Id && c.StationId == station.Id
                && c.ItemId == lot.ItemId && c.LotId == lot.Id, ct);
            if (!recorded)
            {
                db.SerialComponents.Add(SerialComponent.FromRepair(serial.Id, lot.Id, lot.ItemId, PartsPerReplacement, station.Id, repair.Id, now));
                await db.SaveChangesAsync(ct);
            }
            await db.SubtractAsync(lot.Id, PartsPerReplacement, ct);
        }
        await tx.CommitAsync(ct);

        var next = steps.FirstOrDefault(o => o.Seq > serial.CurrentSeq);
        return new RepairResponse(serial.SerialNo, serial.Status.ToString(), next?.Seq, next?.OperationCode);
    }

    // The new part's lot, checked like a reel at loading (quality only). Null when nothing traced was replaced.
    // A label that is not a lot is a form error (400 on the field); a real lot that must not be used is a rule (422).
    private async Task<LotFacts?> ReplacementLotAsync(CompleteRepairCommand cmd, int woId, DateTime now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cmd.ReplacementLotNo))
        {
            if (material.Value.EnforceLoadedMaterial && CompleteRepairValidator.IsReplacement(cmd.Action))
                throw new ValidationException([new ValidationFailure(nameof(cmd.ReplacementLotNo),
                    "A replacement needs the lot of the new part: scan its reel label.")]);
            return null;
        }
        var lotNo = cmd.ReplacementLotNo.Trim().ToUpperInvariant();
        var lot = await db.MaterialLots.AsNoTracking().Where(l => l.LotNo == lotNo).Select(l =>
            new LotFacts(l.Id, l.LotNo, l.ItemId, l.RemainingQty, l.ReceivedAt, l.ExpiryAt, l.Status)).SingleOrDefaultAsync(ct);
        var verdict = MaterialVerifier.VerifyReplacement(lot, await db.RequirementsAsync(woId, ct), now);
        return verdict.Code switch
        {
            VerifyCode.Ok => lot,
            VerifyCode.LotNotFound => throw new ValidationException([new ValidationFailure(nameof(cmd.ReplacementLotNo), $"Lot {lotNo} not found.")]),
            VerifyCode.ItemNotInBom => throw new DomainException($"Lot {lotNo} is not a material of this work order."),
            VerifyCode.LotOnHold => throw new DomainException($"Lot {lotNo} is on hold: take a part from another lot."),
            VerifyCode.LotNotIqcPassed => throw new DomainException($"Lot {lotNo} has not passed incoming inspection."),
            VerifyCode.LotExpired => throw new DomainException($"Lot {lotNo} has expired."),
            _ => throw new InvalidOperationException($"Unexpected verification code {verdict.Code}."),
        };
    }
}

// ---------- Scrap (spec M3 step 4, optional operation) ----------

public sealed record ScrapSerialCommand(string SerialNo, string Reason) : IRequest<SerialDto>;

public sealed class ScrapSerialValidator : AbstractValidator<ScrapSerialCommand>
{
    public ScrapSerialValidator()
    {
        RuleFor(x => x.SerialNo).NotEmpty().MaximumLength(Serial.SerialNoMaxLength);
        RuleFor(x => x.Reason).NotEmpty().MaximumLength(Serial.ScrapReasonMaxLength);
    }
}

// Serials are not audited (high volume), so who scrapped what is written to the log.
public sealed class ScrapSerialHandler(IAppDbContext db, ICurrentUser user, ILogger<ScrapSerialHandler> log)
    : IRequestHandler<ScrapSerialCommand, SerialDto>
{
    public async Task<SerialDto> Handle(ScrapSerialCommand cmd, CancellationToken ct)
    {
        var serialNo = cmd.SerialNo.Trim().ToUpperInvariant();
        var serial = await db.Serials.SingleOrDefaultAsync(s => s.SerialNo == serialNo, ct)
                     ?? throw new KeyNotFoundException($"Serial {serialNo} not found.");
        var before = serial.Status;
        serial.Scrap(cmd.Reason);   // Completed / already Scrapped -> 422
        await db.SaveChangesAsync(ct);
        log.LogInformation("Serial {SerialNo} scrapped from {OldStatus} by {EmployeeCode}: {ScrapReason}",
            serial.SerialNo, before, user.EmployeeCode, serial.ScrapReason);
        return new SerialDto(serial.Id, serial.SerialNo, serial.Status.ToString(), serial.CurrentSeq);
    }
}
