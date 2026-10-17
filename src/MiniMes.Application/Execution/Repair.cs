using FluentValidation;
using FluentValidation.Results;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Contracts.Execution;
using MiniMes.Contracts.Planning;
using MiniMes.Domain.Common;
using MiniMes.Domain.Execution;
using MiniMes.Domain.Factory;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Execution;

// ---------- Complete a repair (spec M3 step 4) ----------

// The board was checked in by a scan at the Repair station (Ng -> Repair). The technician records what was
// done; the board goes back to the failed step, or to an earlier one (rework, ReturnToSeq).
public sealed record CompleteRepairCommand(int StationId, string SerialNo, string DefectCode, string Action,
    string? ComponentReplaced, int? ReturnToSeq) : IRequest<RepairResponse>;

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
            .When(x => x.Action?.TrimStart().StartsWith("Replace", StringComparison.OrdinalIgnoreCase) == true)
            .WithMessage("A replacement needs the component replaced (reference and lot).");
        RuleFor(x => x.ReturnToSeq).GreaterThan(0).When(x => x.ReturnToSeq is not null);
    }
}

public sealed class CompleteRepairHandler(IAppDbContext db, ICurrentUser user, TimeProvider time)
    : IRequestHandler<CompleteRepairCommand, RepairResponse>
{
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
        db.RepairLogs.Add(new RepairLog(serial, station.Id, defectCode, cmd.Action, cmd.ComponentReplaced,
            cmd.ReturnToSeq, technician, time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(ct);   // one save = one transaction; a second "complete" of the same board -> 409 or 422

        var next = steps.FirstOrDefault(o => o.Seq > serial.CurrentSeq);
        return new RepairResponse(serial.SerialNo, serial.Status.ToString(), next?.Seq, next?.OperationCode);
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
