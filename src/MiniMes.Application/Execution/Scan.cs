using System.Diagnostics;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;
using MiniMes.Application.Common;
using MiniMes.Contracts.Execution;
using MiniMes.Domain.Execution;
using MiniMes.Domain.MasterData;
using MiniMes.Domain.Planning;

namespace MiniMes.Application.Execution;

// The scan (spec M3 2.2): read -> route check -> if allowed, write in ONE transaction the serial's new state,
// the ScanLog row and the step counter. Blocked or duplicate scans write nothing. The steps are in ScanExecutor.
public sealed record ScanCommand(int StationId, string SerialNo, string Result, string? DefectCode, string Source)
    : IRequest<ScanResponse>;

// Published inside the scan's transaction after a NEW scan OK at a routing step is saved (never for a duplicate,
// an NG or a Repair check-in). Handlers share the scan's DbContext; one that throws rolls the scan back (M6 2.5).
public sealed record SerialScannedOk(long SerialId, string SerialNo, int WoId, int StationId, int Seq, long ScanLogId, DateTime At)
    : INotification;

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

// M3b step 3: the work itself is in ScanExecutor; this handler is the outermost one, so it owns the transaction.
public sealed class ScanHandler(IAppDbContext db, ScanExecutor scans, ILogger<ScanHandler> log)
    : IRequestHandler<ScanCommand, ScanResponse>
{
    public async Task<ScanResponse> Handle(ScanCommand cmd, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        await using var tx = await db.BeginTransactionAsync(ct);
        var outcome = await scans.ExecuteAsync(tx, cmd, ct);
        if (outcome.Recorded) await tx.CommitAsync(ct);   // blocked / duplicate wrote nothing: dispose rolls back
        var response = outcome.Response;
        log.LogInformation("Scan {SerialNo} at station {StationId}: {ScanResult} {ReasonCode} duplicate={Duplicate} in {ElapsedMs} ms",
            ScanExecutor.Normalize(cmd.SerialNo), cmd.StationId, response.Result, response.ReasonCode, response.Duplicate, watch.ElapsedMilliseconds);
        return response;
    }
}
